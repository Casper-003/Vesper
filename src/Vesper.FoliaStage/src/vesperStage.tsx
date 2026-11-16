/*
 * Vesper Folia Stage Host Adapter
 * Copyright (c) 2026 Casper
 * Modified/adapted on 2026-11-16 from chthollyphile/folia-major (GNU AGPL-3.0).
 * Licensed under the GNU Affero General Public License v3.0 (see /LICENSE).
 */
import { Buffer } from 'buffer';
import React, { Suspense, useCallback, useDeferredValue, useEffect, useMemo, useRef, useState } from 'react';
import ReactDOM from 'react-dom/client';
import { useMotionValue } from 'framer-motion';
import { useTranslation } from 'react-i18next';
import i18n from './i18n/config';
import './index.css';
import { installGlobalVisualizerFrameRateLimiter } from './utils/frameRateLimiter';
import VisualizerRenderer from './components/visualizer/VisualizerRenderer';
import {
  VISUALIZER_REGISTRY,
  getVisualizerModeLabel,
  hasVisualizerMode,
} from './components/visualizer/registry';
import {
  VISUALIZER_BACKGROUND_REGISTRY,
  DEFAULT_VISUALIZER_BACKGROUND_MODE,
  getVisualizerBackgroundModeLabel,
  hasVisualizerBackgroundMode,
} from './components/visualizer/backgrounds/registry';
import {
  parseLyricsByFormat,
  finalizeParsedLyricLines,
  type LyricParseFormat,
} from './utils/lyrics/parserCore';
import { buildTimedWords } from './utils/lyrics/estimatedLineTiming';
import { detectTimedLyricFormat } from './utils/lyrics/formatDetection';
import { NO_LYRIC_LINES } from './utils/lyrics/noLyricLines';
import { findLatestActiveLineIndex } from './utils/appPlaybackHelpers';
import { generateBuiltinDualTheme } from './utils/builtinTheme/generateBuiltinDualTheme';
import { extractColors } from './utils/colorExtractor';
import type {
  Line,
  Theme,
  VisualizerBackgroundMode,
  VisualizerMode,
  Word,
} from './types';

// @ts-ignore
globalThis.Buffer = Buffer;
installGlobalVisualizerFrameRateLimiter();

const MODE_STORAGE_KEY = 'vesper_folia_visualizer_mode';
const BG_MODE_STORAGE_KEY = 'vesper_folia_background_mode';
const EMPTY_SPECTRUM = new Uint8Array(0);

interface HostSyllablePayload {
  startMs: number;
  durationMs: number;
  text: string;
}

interface HostLinePayload {
  startMs: number;
  endMs: number;
  text: string;
  translation?: string | null;
  isWordSynced?: boolean;
  syllables?: HostSyllablePayload[];
}

interface HostSongPayload {
  songKey: string;
  title: string;
  artist: string;
  album?: string | null;
  coverUrl?: string | null;
  paletteHex?: string[];
  durationSeconds?: number;
  offsetMs?: number;
  loadingLyrics?: boolean;
  canSeek?: boolean;
  rawLyrics?: string | null;
  translationLyrics?: string | null;
  rawFormat?: string | null;
  fallbackLines?: HostLinePayload[];
}

interface HostClockPayload {
  positionMs: number;
  paused: boolean;
  durationSeconds?: number;
}

type HostMessage =
  | { type: 'song'; payload: HostSongPayload }
  | { type: 'clock'; payload: HostClockPayload }
  | { type: 'chrome'; payload: { controlsVisible: boolean } }
  | { type: 'active'; payload: { active: boolean } };

declare global {
  interface Window {
    chrome?: {
      webview?: {
        postMessage: (message: unknown) => void;
        addEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void;
        removeEventListener: (type: 'message', listener: (event: MessageEvent) => void) => void;
      };
    };
    __vesperReceive?: (msg: unknown) => void;
    __vesperQueue?: unknown[];
  }
}

function postToHost(payload: Record<string, unknown>): void {
  try {
    window.chrome?.webview?.postMessage(payload);
  } catch {
    // No-op when previewed outside WebView2.
  }
}

function unwrapQrcXml(raw: string): string {
  const match = raw.match(/LyricContent="([^"]+)"/i);
  if (!match) return raw;
  return match[1]
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&');
}

function normalizeFormat(rawFormat: string | null | undefined, rawText: string): LyricParseFormat {
  const trimmed = rawText.trim();
  const fmt = (rawFormat ?? '').trim().toLowerCase();
  if (fmt === 'ttml' || /<tt(?:\s|>)/i.test(trimmed)) return 'ttml';
  if (fmt === 'yrc') return 'yrc';
  if (fmt === 'qrc' || /LyricContent="/i.test(trimmed) || /^\[\d+,\d+\].*\(\d+,\d+\)/m.test(trimmed)) return 'qrc';
  if (fmt === 'krc' || /^\[\d+,\d+\].*<\d+,\d+(?:,\d+)?>/m.test(trimmed)) return 'krc';
  if (fmt === 'vtt') return 'vtt';
  if (fmt === 'enhancedlrc' || fmt === 'enhanced-lrc') return 'enhanced-lrc';
  if (fmt === 'awlrc') return 'awlrc';
  return detectTimedLyricFormat(trimmed);
}

function convertFallbackLines(rawLines: HostLinePayload[] | undefined): Line[] {
  if (!rawLines || rawLines.length === 0) return NO_LYRIC_LINES;

  const timedRows = rawLines.filter(l => l.endMs > 0 || l.startMs > 0 || (l.syllables && l.syllables.length > 0));
  if (timedRows.length === 0) {
    // Plain unsynced lyrics: distribute evenly or emit static non-timed lines so lyrics still appear.
    return rawLines
      .filter(l => l.text.trim().length > 0)
      .map((l, idx) => ({
        startTime: idx * 3,
        endTime: (idx + 1) * 3,
        fullText: l.text.trim(),
        translation: l.translation?.trim() || undefined,
        words: [{ text: l.text.trim(), startTime: idx * 3, endTime: (idx + 1) * 3 }],
      }));
  }

  const lines: Line[] = [];
  for (let i = 0; i < rawLines.length; i += 1) {
    const item = rawLines[i];
    const startTime = Math.max(0, item.startMs / 1000);
    const nextStart = rawLines[i + 1]?.startMs ? Math.max(startTime + 0.1, rawLines[i + 1].startMs / 1000) : startTime + 5;
    const endTime = Math.max(startTime + 0.08, item.endMs > item.startMs ? item.endMs / 1000 : nextStart);
    const words: Word[] = item.syllables && item.syllables.length > 0
      ? item.syllables.map(s => {
          const ws = Math.max(0, s.startMs / 1000);
          const we = Math.max(ws + 0.02, (s.startMs + Math.max(10, s.durationMs)) / 1000);
          return { text: s.text, startTime: ws, endTime: we };
        })
      : buildTimedWords(item.text, startTime, endTime);

    lines.push({
      startTime,
      endTime,
      fullText: item.text,
      translation: item.translation?.trim() || undefined,
      words,
    });
  }

  return finalizeParsedLyricLines(lines, { includeInterludes: true });
}

function buildStageLyrics(song: HostSongPayload | null): Line[] {
  if (!song) return NO_LYRIC_LINES;

  const rawText = (song.rawLyrics ?? '').trim();
  const transText = (song.translationLyrics ?? '').trim();
  const fallbackConverted = convertFallbackLines(song.fallbackLines);
  const fallbackHasWords = song.fallbackLines?.some(l => l.isWordSynced && (l.syllables?.length ?? 0) > 1) ?? false;

  if (rawText.length > 0) {
    try {
      const format = normalizeFormat(song.rawFormat, rawText);
      const normalizedRaw = format === 'qrc' ? unwrapQrcXml(rawText) : rawText;
      const normalizedTrans = format === 'qrc' && transText ? unwrapQrcXml(transText) : transText;
      const foliaParsed = parseLyricsByFormat(format, normalizedRaw, normalizedTrans, { includeInterludes: true });

      if (foliaParsed.lines.length > 0) {
        if (format === 'ttml') {
          return foliaParsed.lines;
        }
        if (foliaParsed.isWordByWord || !fallbackHasWords) {
          return foliaParsed.lines;
        }
      }
    } catch (error) {
      console.warn('[VesperStage] Folia parser fallback triggered:', error);
    }
  }

  return fallbackConverted.length > 0 ? fallbackConverted : NO_LYRIC_LINES;
}

function readStoredMode(): VisualizerMode {
  try {
    const saved = localStorage.getItem(MODE_STORAGE_KEY);
    if (saved && hasVisualizerMode(saved)) return saved;
  } catch {
    // Ignore storage read failure.
  }
  return 'classic';
}

function readStoredBackgroundMode(): VisualizerBackgroundMode {
  try {
    const saved = localStorage.getItem(BG_MODE_STORAGE_KEY);
    if (saved && saved !== 'url' && hasVisualizerBackgroundMode(saved)) return saved;
  } catch {
    // Ignore storage read failure.
  }
  return DEFAULT_VISUALIZER_BACKGROUND_MODE;
}

const MODE_ORDER = VISUALIZER_REGISTRY;
const BG_OPTIONS = VISUALIZER_BACKGROUND_REGISTRY.filter(entry => entry.mode !== 'url');

const StageLoadingBanner: React.FC<{ label: string }> = ({ label }) => (
  <div className="pointer-events-none fixed inset-x-0 top-6 z-50 flex justify-center">
    <div className="flex items-center gap-2.5 rounded-full border border-white/15 bg-black/55 px-4 py-1.5 text-xs text-white/90 shadow-lg backdrop-blur-xl">
      <span className="h-3 w-3 animate-spin rounded-full border-2 border-white/25 border-t-white/90" />
      <span>{label}</span>
    </div>
  </div>
);

const VesperStageApp: React.FC = () => {
  const { t } = useTranslation();

  const [song, setSong] = useState<HostSongPayload | null>(null);
  const [paused, setPaused] = useState(true);
  const [active, setActive] = useState(true);
  const [controlsVisible, setControlsVisible] = useState(false);
  const [duration, setDuration] = useState(0);
  const [currentLineIndex, setCurrentLineIndex] = useState(-1);

  const [mode, setMode] = useState<VisualizerMode>(readStoredMode);
  const deferredMode = useDeferredValue(mode);
  const isSwitchingMode = mode !== deferredMode;

  const [bgMode, setBgMode] = useState<VisualizerBackgroundMode>(readStoredBackgroundMode);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [pointerActive, setPointerActive] = useState(false);

  const [extractedCoverColors, setExtractedCoverColors] = useState<string[]>([]);

  const currentLineIndexRef = useRef(-1);
  const linesRef = useRef<Line[]>(NO_LYRIC_LINES);
  const offsetMsRef = useRef(0);
  const pausedRef = useRef(true);
  const activeRef = useRef(true);
  const durationRef = useRef(0);
  const clockRef = useRef<{ positionSeconds: number; perfMs: number }>({
    positionSeconds: 0,
    perfMs: performance.now(),
  });
  const hideTimerRef = useRef<number | null>(null);
  const lastInteractPostRef = useRef(0);

  const currentTime = useMotionValue(0);
  const audioPower = useMotionValue(0);
  const bass = useMotionValue(0);
  const lowMid = useMotionValue(0);
  const mid = useMotionValue(0);
  const vocal = useMotionValue(0);
  const treble = useMotionValue(0);
  const spectrum = useMotionValue(EMPTY_SPECTRUM);
  const audioBands = useMemo(() => ({
    bass,
    lowMid,
    mid,
    vocal,
    treble,
    spectrum,
  }), [bass, lowMid, mid, spectrum, treble, vocal]);

  useEffect(() => {
    if (!localStorage.getItem('folia_app_language')) {
      void i18n.changeLanguage('zh-CN');
    }
    document.documentElement.classList.add('dark');
  }, []);

  const bumpPointerActivity = useCallback(() => {
    setPointerActive(true);
    if (hideTimerRef.current !== null) {
      window.clearTimeout(hideTimerRef.current);
    }
    hideTimerRef.current = window.setTimeout(() => {
      setPointerActive(false);
    }, 3000);

    const now = performance.now();
    if (now - lastInteractPostRef.current > 180) {
      lastInteractPostRef.current = now;
      postToHost({ type: 'interact' });
    }
  }, []);

  useEffect(() => {
    const onPointerMove = () => bumpPointerActivity();
    const onPointerDown = () => bumpPointerActivity();
    const onKeyDown = () => bumpPointerActivity();

    window.addEventListener('pointermove', onPointerMove, { passive: true });
    window.addEventListener('pointerdown', onPointerDown, { passive: true });
    window.addEventListener('keydown', onKeyDown, { passive: true });
    return () => {
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('pointerdown', onPointerDown);
      window.removeEventListener('keydown', onKeyDown);
      if (hideTimerRef.current !== null) window.clearTimeout(hideTimerRef.current);
    };
  }, [bumpPointerActivity]);

  const handleHostMessage = useCallback((raw: unknown) => {
    let msg: HostMessage | null = null;
    if (typeof raw === 'string') {
      try {
        msg = JSON.parse(raw) as HostMessage;
      } catch {
        return;
      }
    } else if (raw && typeof raw === 'object') {
      msg = raw as HostMessage;
    }
    if (!msg || typeof msg.type !== 'string') return;

    if (msg.type === 'song') {
      const next = msg.payload;
      offsetMsRef.current = next.offsetMs ?? 0;
      if (typeof next.durationSeconds === 'number' && Number.isFinite(next.durationSeconds)) {
        durationRef.current = next.durationSeconds;
        setDuration(next.durationSeconds);
      }
      setSong(next);
    } else if (msg.type === 'clock') {
      const { positionMs, paused: nextPaused, durationSeconds } = msg.payload;
      clockRef.current = {
        positionSeconds: Math.max(0, (positionMs ?? 0) / 1000),
        perfMs: performance.now(),
      };
      pausedRef.current = Boolean(nextPaused);
      setPaused(Boolean(nextPaused));
      if (typeof durationSeconds === 'number' && Number.isFinite(durationSeconds) && durationSeconds > 0) {
        durationRef.current = durationSeconds;
        setDuration(durationSeconds);
      }
    } else if (msg.type === 'chrome') {
      setControlsVisible(Boolean(msg.payload?.controlsVisible));
    } else if (msg.type === 'active') {
      activeRef.current = Boolean(msg.payload?.active);
      setActive(Boolean(msg.payload?.active));
    }
  }, []);

  useEffect(() => {
    window.__vesperReceive = handleHostMessage;

    const webview = window.chrome?.webview;
    const onWebMessage = (event: MessageEvent) => handleHostMessage(event.data);
    webview?.addEventListener('message', onWebMessage);

    if (Array.isArray(window.__vesperQueue)) {
      const pending = window.__vesperQueue.splice(0);
      for (const item of pending) handleHostMessage(item);
    }

    postToHost({ type: 'ready' });
    return () => {
      webview?.removeEventListener('message', onWebMessage);
    };
  }, [handleHostMessage]);

  useEffect(() => {
    let cancelled = false;
    const url = song?.coverUrl?.trim();
    if (!url) {
      setExtractedCoverColors([]);
      return;
    }

    void extractColors(url, 5).then(colors => {
      if (!cancelled && colors.length > 0) {
        setExtractedCoverColors(colors);
      }
    }).catch(() => {
      if (!cancelled) setExtractedCoverColors([]);
    });

    return () => {
      cancelled = true;
    };
  }, [song?.coverUrl]);

  const theme: Theme = useMemo(() => {
    const colors = extractedCoverColors.length > 0
      ? extractedCoverColors
      : (song?.paletteHex && song.paletteHex.length > 0
        ? song.paletteHex
        : ['#39c5bb', '#4f7cff', '#8b5cf6']);
    return generateBuiltinDualTheme({
      coverColors: colors,
      random: () => 0.42,
    }).dark;
  }, [extractedCoverColors, song?.paletteHex]);

  const lines: Line[] = useMemo(() => buildStageLyrics(song), [song]);

  useEffect(() => {
    linesRef.current = lines;
    currentLineIndexRef.current = -1;
    setCurrentLineIndex(-1);
  }, [lines]);

  useEffect(() => {
    if (!active) return;

    let rafId = 0;
    let smoothedEnergy = 0;

    const tick = (now: number) => {
      const { positionSeconds, perfMs } = clockRef.current;
      const isPaused = pausedRef.current;
      const rawPos = isPaused
        ? positionSeconds
        : positionSeconds + Math.max(0, (now - perfMs) / 1000);
      const bounded = durationRef.current > 0 ? Math.min(rawPos, durationRef.current) : rawPos;
      const lyricTime = Math.max(0, bounded + (offsetMsRef.current / 1000));

      currentTime.set(lyricTime);

      const currentLines = linesRef.current;
      const nextIndex = currentLines.length > 0 ? findLatestActiveLineIndex(currentLines, lyricTime) : -1;
      if (nextIndex !== currentLineIndexRef.current) {
        currentLineIndexRef.current = nextIndex;
        setCurrentLineIndex(nextIndex);
      }

      // Gentle synthetic vocal/beat energy so reactive shaders breathe with lyrics & playback even without decoded PCM FFT.
      const tSec = now / 1000;
      const activeLine = nextIndex >= 0 ? currentLines[nextIndex] : null;
      const singingBoost = !isPaused && activeLine && activeLine.fullText !== '......' ? 1.0 : 0.45;
      const targetEnergy = isPaused ? 0 : singingBoost * (0.42 + 0.25 * Math.sin(tSec * 2.1) + 0.14 * Math.sin(tSec * 4.7));
      smoothedEnergy += (targetEnergy - smoothedEnergy) * (isPaused ? 0.08 : 0.14);

      const power = Math.max(0, Math.min(255, smoothedEnergy * 155));
      audioPower.set(power);
      bass.set(Math.max(0, Math.min(255, power * (0.95 + 0.2 * Math.sin(tSec * 3.1)))));
      lowMid.set(Math.max(0, Math.min(255, power * (0.85 + 0.18 * Math.cos(tSec * 2.5)))));
      mid.set(Math.max(0, Math.min(255, power * (0.9 + 0.16 * Math.sin(tSec * 3.8)))));
      vocal.set(Math.max(0, Math.min(255, power * (activeLine ? 1.08 : 0.55))));
      treble.set(Math.max(0, Math.min(255, power * (0.68 + 0.22 * Math.cos(tSec * 5.2)))));

      rafId = window.requestAnimationFrame(tick);
    };

    rafId = window.requestAnimationFrame(tick);
    return () => window.cancelAnimationFrame(rafId);
  }, [active, audioPower, bass, currentTime, lowMid, mid, treble, vocal]);

  const backgroundConfig = useMemo(() => ({
    mode: bgMode,
    common: {
      useCoverColorBg: true,
      opacity: 0.78,
      disableGeometricBackground: false,
      disableVignette: false,
    },
  }), [bgMode]);

  const handleSelectMode = useCallback((nextMode: VisualizerMode) => {
    setMode(nextMode);
    try {
      localStorage.setItem(MODE_STORAGE_KEY, nextMode);
    } catch {
      // Ignore.
    }
  }, []);

  const handleSelectBg = useCallback((nextBg: VisualizerBackgroundMode) => {
    setBgMode(nextBg);
    try {
      localStorage.setItem(BG_MODE_STORAGE_KEY, nextBg);
    } catch {
      // Ignore.
    }
  }, []);

  const handleSeek = useCallback((lyricTimeSec: number) => {
    if (!(song?.canSeek ?? true)) return;
    const rawSec = Math.max(0, lyricTimeSec - (offsetMsRef.current / 1000));
    postToHost({ type: 'seek', timeSeconds: rawSec });
  }, [song?.canSeek]);

  const chromeShown = pickerOpen || pointerActive || controlsVisible;
  const activeModeLabel = getVisualizerModeLabel(mode, t);
  const activeBgLabel = getVisualizerBackgroundModeLabel(bgMode, t);
  const loadingMessage = isSwitchingMode
    ? `正在加载「${activeModeLabel}」渲染器…`
    : song?.loadingLyrics
      ? '正在匹配在线歌词…'
      : null;
  const showNoLyricsPrompt = song !== null && !song.loadingLyrics && lines.length === 0;

  return (
    <div
      className="relative h-screen w-screen overflow-hidden"
      style={{ backgroundColor: theme.backgroundColor, color: theme.primaryColor }}
    >
      <Suspense fallback={<StageLoadingBanner label={`正在准备 Folia「${activeModeLabel}」舞台…`} />}>
        <VisualizerRenderer
          mode={mode}
          animateStyleSwitch={true}
          currentTime={currentTime}
          duration={duration}
          currentLineIndex={currentLineIndex}
          lines={lines}
          theme={theme}
          isDaylight={false}
          audioPower={audioPower}
          audioBands={audioBands}
          songTitle={song?.title}
          songArtist={song?.artist}
          songAlbum={song?.album}
          coverUrl={song?.coverUrl}
          showText={true}
          seed={song?.songKey ?? 'vesper'}
          paused={paused}
          background={backgroundConfig}
          isPlayerChromeHidden={!controlsVisible}
          showSubtitleTranslation={true}
          showHarmonySubtitle={true}
          harmonySubtitleBackground={true}
          onLyricLineSeek={song?.canSeek ?? true ? handleSeek : undefined}
        />
      </Suspense>

      {loadingMessage && <StageLoadingBanner label={loadingMessage} />}

      {showNoLyricsPrompt && (
        <div className="pointer-events-none fixed inset-0 z-30 flex flex-col items-center justify-center gap-4 px-8 text-center">
          <div className="max-w-lg text-2xl font-semibold tracking-tight text-white/85 drop-shadow">
            {song?.title}
          </div>
          {song?.artist && (
            <div className="text-sm text-white/60">
              {song.artist}
            </div>
          )}
          <div className="text-sm text-white/55">
            暂无同步歌词
          </div>
          <button
            type="button"
            onClick={() => postToHost({ type: 'manage-lyrics' })}
            className="pointer-events-auto cursor-pointer rounded-full border border-white/20 bg-white/10 px-5 py-2 text-sm font-medium text-white backdrop-blur-xl transition hover:bg-white/20"
          >
            手动匹配／修改歌词
          </button>
        </div>
      )}

      {/* Top-right auto-hiding Folia mode & background switcher */}
      <div
        className={`fixed right-7 top-6 z-40 flex flex-col items-end gap-2 transition-opacity duration-300 ${
          chromeShown ? 'opacity-100' : 'pointer-events-none opacity-0'
        }`}
      >
        <div className="flex items-center gap-2">
          <button
            type="button"
            onClick={() => {
              bumpPointerActivity();
              setPickerOpen(prev => !prev);
            }}
            className="flex cursor-pointer items-center gap-2 rounded-full border border-white/15 bg-black/45 px-3.5 py-1.5 text-xs font-medium text-white/90 shadow-lg backdrop-blur-xl transition hover:border-white/30 hover:bg-black/65"
          >
            <span className="opacity-70">Folia 样式</span>
            <span>{activeModeLabel}</span>
            <span className="opacity-40">·</span>
            <span className="opacity-85">{activeBgLabel}</span>
          </button>
        </div>

        {pickerOpen && (
          <div
            className="w-[340px] max-w-[calc(100vw-3rem)] rounded-2xl border border-white/15 bg-zinc-950/85 p-3.5 text-xs text-white shadow-2xl backdrop-blur-2xl"
            onClick={e => e.stopPropagation()}
          >
            <div className="mb-2 flex items-center justify-between text-[11px] font-medium text-white/60">
              <span>歌词视觉样式</span>
              <button
                type="button"
                onClick={() => setPickerOpen(false)}
                className="cursor-pointer rounded px-1.5 py-0.5 text-white/60 hover:bg-white/10 hover:text-white"
              >
                收起
              </button>
            </div>
            <div className="grid grid-cols-3 gap-1.5">
              {MODE_ORDER.map(entry => {
                const selected = entry.mode === mode;
                return (
                  <button
                    key={entry.mode}
                    type="button"
                    onClick={() => handleSelectMode(entry.mode)}
                    className={`cursor-pointer truncate rounded-lg border px-2 py-1.5 text-left transition ${
                      selected
                        ? 'border-white/55 bg-white/20 font-semibold text-white'
                        : 'border-white/10 bg-white/5 text-white/80 hover:border-white/25 hover:bg-white/10'
                    }`}
                  >
                    {getVisualizerModeLabel(entry.mode, t)}
                  </button>
                );
              })}
            </div>

            <div className="mb-2 mt-3 text-[11px] font-medium text-white/60">背景氛围</div>
            <div className="grid grid-cols-3 gap-1.5">
              {BG_OPTIONS.map(entry => {
                const selected = entry.mode === bgMode;
                return (
                  <button
                    key={entry.mode}
                    type="button"
                    onClick={() => handleSelectBg(entry.mode)}
                    className={`cursor-pointer truncate rounded-lg border px-2 py-1.5 text-left transition ${
                      selected
                        ? 'border-white/55 bg-white/20 font-semibold text-white'
                        : 'border-white/10 bg-white/5 text-white/80 hover:border-white/25 hover:bg-white/10'
                    }`}
                  >
                    {getVisualizerBackgroundModeLabel(entry.mode, t)}
                  </button>
                );
              })}
            </div>
            <div className="mt-2.5 border-t border-white/10 pt-2 text-[10px] text-white/45">
              视觉引擎基于 Folia (GNU AGPL-3.0)
            </div>
          </div>
        )}
      </div>
    </div>
  );
};

const rootElement = document.getElementById('root');
if (rootElement) {
  ReactDOM.createRoot(rootElement).render(
    <React.StrictMode>
      <VesperStageApp />
    </React.StrictMode>,
  );
}
