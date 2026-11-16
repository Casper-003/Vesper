import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { type UserConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { commandPinyinPlugin } from './dev/pinyin/commandPinyinPlugin.mjs';
import { traditionalLocalePlugin } from './dev/i18n/traditionalLocalePlugin.mjs';
import { icuFoliaDataPlugin } from './dev/icuFolia/icuFoliaDataPlugin.mjs';
import { nebbiaStrokesPlugin } from './dev/nebbia/nebbiaStrokesPlugin.mjs';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const pkg = JSON.parse(fs.readFileSync(path.resolve(__dirname, 'package.json'), 'utf-8'));

export default function viteVesperConfig(): UserConfig {
  return {
    base: './',
    worker: {
      format: 'es',
    },
    build: {
      outDir: path.resolve(__dirname, '../../src/Vesper.App/Assets/FoliaStage'),
      emptyOutDir: true,
      chunkSizeWarningLimit: 3000,
      rollupOptions: {
        input: {
          vesperStage: path.resolve(__dirname, 'vesper-stage.html'),
        },
        output: {
          codeSplitting: {
            groups: [
              { name: 'three', test: /[\\/]node_modules[\\/]three[\\/]/ },
              {
                name: 'lucide-icons',
                test: /[\\/]node_modules[\\/]lucide-react[\\/]dist[\\/]esm[\\/]icons[\\/]/,
                minShareCount: 1,
              },
            ],
          },
          chunkFileNames: 'assets/[name]-[hash].js',
        },
      },
    },
    plugins: [
      commandPinyinPlugin(),
      traditionalLocalePlugin(),
      icuFoliaDataPlugin(),
      nebbiaStrokesPlugin(),
      react(),
    ],
    define: {
      '__COMMIT_HASH__': JSON.stringify('vesper-embedded'),
      '__GIT_BRANCH__': JSON.stringify('main'),
      '__BUILD_REPO__': JSON.stringify('chthollyphile/folia-major'),
      '__BUILD_COMMIT__': JSON.stringify('vesper-embedded'),
      '__BUILD_KIND__': JSON.stringify('self-hosted'),
      '__BUILD_HOST__': JSON.stringify('vesper'),
      '__APP_VERSION__': JSON.stringify(pkg.version),
      '__APP_VERSION_LABEL__': JSON.stringify('Realeco'),
      '__APP_RELEASE_CHANNEL__': JSON.stringify('realeco'),
      '__DOCKER_STACK_VERSION__': JSON.stringify(''),
    },
    resolve: {
      alias: {
        '@': path.resolve(__dirname, './src'),
      },
    },
  };
}
