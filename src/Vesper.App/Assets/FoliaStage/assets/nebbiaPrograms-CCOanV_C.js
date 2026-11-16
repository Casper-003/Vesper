var e={edge:.6,beads:.35,drip:.25,refract:1,glint:1,fieldDrops:.5,wind:0,endBead:1,endBeadMin:48,beadHold:4,breathSpread:1.2,breathHold:1,breathFade:8,breathThick:.7,bumpRate:1,bumpFall:.5},t=.5,n=(e,t=0,n=1)=>Math.max(t,Math.min(n,e)),r=e=>e*e*(3-2*e),i=(e,t,n)=>{let r=e.createShader(t);return e.shaderSource(r,n),e.compileShader(r),r},a=(e,t,n,r)=>{let i=e.createProgram();e.attachShader(i,t),e.attachShader(i,n);for(let[t,n]of Object.entries(r))e.bindAttribLocation(i,n,t);return e.linkProgram(i),i},o=(e,t)=>{if(e.getProgramParameter(t,e.LINK_STATUS))return;let n=(e.getAttachedShaders(t)??[]).map(t=>e.getShaderInfoLog(t)).filter(Boolean).join(`
`);throw Error(`Nebbia program: ${e.getProgramInfoLog(t)}${n?`\n${n}`:``}`)},s=(e,t)=>{let n=e.createTexture();return e.activeTexture(e.TEXTURE0+t),e.bindTexture(e.TEXTURE_2D,n),e.texParameteri(e.TEXTURE_2D,e.TEXTURE_MIN_FILTER,e.LINEAR),e.texParameteri(e.TEXTURE_2D,e.TEXTURE_MAG_FILTER,e.LINEAR),e.texParameteri(e.TEXTURE_2D,e.TEXTURE_WRAP_S,e.CLAMP_TO_EDGE),e.texParameteri(e.TEXTURE_2D,e.TEXTURE_WRAP_T,e.CLAMP_TO_EDGE),{t:n,unit:t}},c=(e,t,n,r)=>{let i=n[r],a=n[r+1],o=n[r+2]-i,s=n[r+3]-a,c=o*o+s*s,l=c>0?Math.max(0,Math.min(1,((e-i)*o+(t-a)*s)/c)):0;return Math.hypot(e-i-o*l,t-a-s*l)},l=class{constructor(){this.data=new Float32Array(24576),this.count=0,this.a=1,this.b=0,this.c=0,this.d=1,this.e=0,this.f=0,this.k=1,this.path=[]}clear(){this.count=0,this.path.length=0}setTransform(e,t,n,r,i,a){this.a=e,this.b=t,this.c=n,this.d=r,this.e=i,this.f=a,this.k=Math.hypot(e,t)}moveTo(e,t){this.path.length&&this.path.push(NaN,NaN),this.lineTo(e,t)}lineTo(e,t){this.path.push(this.a*e+this.c*t+this.e,this.b*e+this.d*t+this.f)}stroke(e,t){let n=this.path,r=e*this.k;for(let e=2;e<n.length;e+=2)Number.isNaN(n[e])||Number.isNaN(n[e-2])||this.push(n[e-2],n[e-1],n[e],n[e+1],r,t);n.length=0}push(e,t,n,r,i,a){if((this.count+1)*6>this.data.length){let e=new Float32Array(this.data.length*2);e.set(this.data),this.data=e}let o=this.count*6,s=this.data;s[o]=e,s[o+1]=t,s[o+2]=n,s[o+3]=r,s[o+4]=i,s[o+5]=a,this.count+=1}},u=`#version 300 es
in vec2 aCorner; in vec4 aSeg; in vec2 aParam;
uniform vec2 uView;
out vec2 vP; flat out vec4 vSeg; flat out vec2 vParam;
void main() {
  vec2 a = aSeg.xy, b = aSeg.zw, d = b - a;
  float l = length(d);
  vec2 t = l > 1e-5 ? d / l : vec2(1.0, 0.0), n = vec2(-t.y, t.x);
  // 最宽那层的半宽再留 1 像素抗锯齿
  float R = aParam.x * 0.65 + 1.0;
  vec2 p = (a + b) * 0.5 + t * aCorner.x * (l * 0.5 + R) + n * aCorner.y * R;
  vP = p; vSeg = aSeg; vParam = aParam;
  gl_Position = vec4(p / uView * 2.0 - 1.0, 0.0, 1.0);
}`,d=`#version 300 es
precision highp float;
in vec2 vP; flat in vec4 vSeg; flat in vec2 vParam;
out vec4 o;
// 半宽 r 的线离像素中心 d 时的覆盖度（1 像素线性过渡，接近画布的抗锯齿）
float cover(float r, float d) { return clamp(r + 0.5 - d, 0.0, 1.0); }
// 画布的颜色是 8 位：先量化再乘覆盖度
float level(float x) { return floor(x * 255.0 + 0.5) / 255.0; }
void main() {
  vec2 a = vSeg.xy, ba = vSeg.zw - a, pa = vP - a;
  float h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
  float d = length(pa - ba * h), w = vParam.x * 0.5, v = vParam.y;
  // 三层按画布的 lighten 一层层叠：每层都比下面亮，覆盖度 a 的像素是 m + a × (这层 − m)
  float m = level(v * 0.38) * cover(w * 1.3, d);
  m += cover(w, d) * (level(v * 0.72) - m);
  m += cover(w * 0.62, d) * (level(v) - m);
  o = vec4(m, 0.0, 0.0, 1.0);
}`,f=class{constructor(e,t){this.gl=e,this.prog=null,this.uView=null,this.w=1,this.h=1,this.tex=s(e,t),this.fb=e.createFramebuffer(),this.vao=e.createVertexArray(),e.bindVertexArray(this.vao),e.bindBuffer(e.ARRAY_BUFFER,e.createBuffer()),e.bufferData(e.ARRAY_BUFFER,new Float32Array([-1,-1,1,-1,-1,1,1,1]),e.STATIC_DRAW),e.enableVertexAttribArray(0),e.vertexAttribPointer(0,2,e.FLOAT,!1,0,0),this.buf=e.createBuffer(),e.bindBuffer(e.ARRAY_BUFFER,this.buf),e.enableVertexAttribArray(1),e.vertexAttribPointer(1,4,e.FLOAT,!1,24,0),e.vertexAttribDivisor(1,1),e.enableVertexAttribArray(2),e.vertexAttribPointer(2,2,e.FLOAT,!1,24,16),e.vertexAttribDivisor(2,1),e.bindVertexArray(null)}attach(e){this.prog=e,this.uView=this.gl.getUniformLocation(e,`uView`)}resize(e,n){let{gl:r}=this;this.w=Math.max(1,Math.round(e*t)),this.h=Math.max(1,Math.round(n*t)),r.activeTexture(r.TEXTURE0+this.tex.unit),r.bindTexture(r.TEXTURE_2D,this.tex.t),r.texImage2D(r.TEXTURE_2D,0,r.R8,this.w,this.h,0,r.RED,r.UNSIGNED_BYTE,null),r.bindFramebuffer(r.FRAMEBUFFER,this.fb),r.framebufferTexture2D(r.FRAMEBUFFER,r.COLOR_ATTACHMENT0,r.TEXTURE_2D,this.tex.t,0),r.bindFramebuffer(r.FRAMEBUFFER,null)}draw(e){let{gl:t}=this;t.bindFramebuffer(t.FRAMEBUFFER,this.fb),t.viewport(0,0,this.w,this.h),t.clearColor(0,0,0,0),t.clear(t.COLOR_BUFFER_BIT),e.count&&this.prog&&(t.useProgram(this.prog),t.bindVertexArray(this.vao),t.bindBuffer(t.ARRAY_BUFFER,this.buf),t.bufferData(t.ARRAY_BUFFER,e.data.subarray(0,e.count*6),t.DYNAMIC_DRAW),t.uniform2f(this.uView,this.w,this.h),t.enable(t.BLEND),t.blendEquation(t.MAX),t.drawArraysInstanced(t.TRIANGLE_STRIP,0,4,e.count),t.blendEquation(t.FUNC_ADD),t.disable(t.BLEND),t.bindVertexArray(null)),t.bindFramebuffer(t.FRAMEBUFFER,null)}},p=`
uniform float uBubbles;
// 跟拍冒的每排：x、冒出的时刻、大小（0 是空格）、种子
uniform vec4 uBurst[12];
uniform int uBurstN;
// 1 时舞潮踏拍的气泡一项开着：不冒随机的大泡
uniform float uBeatRows;
// 一颗泡（d 是到泡心的偏移，y 朝下）：rgb 加上去的光，a 泡里压暗多少
vec4 aqBubble(vec2 d, float rad, float b) {
  float l = length(d), aa = max(b, 0.0005);
  float keep = rad * rad / (rad * rad + b * b * 4.0);
  float disc = smoothstep(rad + aa, rad - aa, l);
  float up = 0.5 + 0.5 * clamp(-d.y / max(l, 1e-5), -1.0, 1.0);
  float fres = pow(clamp(l / rad, 0.0, 1.0), 5.0) * disc * (0.35 + 0.65 * up);
  float spec = glowPt(d + vec2(rad * 0.36, rad * 0.4), rad * 0.15, b, 0.0);
  float spec2 = glowPt(d - vec2(rad * 0.32, rad * 0.42), rad * 0.1, b, 0.0) * 0.3;
  float caus = glowPt(d - vec2(0.0, rad * 0.5), rad * 0.32, b, 0.0) * 0.14;
  vec4 big = vec4(vec3(fres * 0.9 * keep + spec * 1.3 + spec2 + caus), disc * 0.14 * keep);
  vec4 tiny = vec4(vec3(glowPt(d, rad * 0.6, b, 0.0) * 1.3 + spec * 1.2), 0.0);
  return mix(tiny, big, smoothstep(0.0018, 0.0034, rad));
}
vec3 aqBubbles(vec3 col, vec2 q, float b, vec3 bc) {
  float t = uTime;
  float drift = sceneryOn(SCN_RAIN_SLANT) ? 0.1 : 0.0;
  float fade = smoothstep(0.93, 0.8, q.y);
  float qx = q.x + (0.92 - q.y) * drift;
  // 细泡：每股从一个泉眼冒出，越往上越散、泡越大一点
  if (sceneryOn(SCN_RAIN)) {
    float cw = 0.055, cx = floor(qx / cw);
    for (float k = -1.0; k <= 1.0; k++) {
      float n = cx + k;
      if (h1(n * 2.3 + 1.0) > uBubbles * 0.9) continue;
      float src = (n + 0.5) * cw + (h1(n + 3.0) - 0.5) * cw * 0.5;
      float sp = 0.14 + 0.1 * h1(n + 7.0), ch = 0.014 + 0.01 * h1(n + 5.0);
      float rise = clamp((0.92 - q.y) / 0.9, 0.0, 1.0);
      float sw = 0.003 + 0.016 * rise, swe = sqrt(sw * sw + b * b);
      float sx = qx - src;
      col += bc * exp(-sx * sx / (swe * swe)) * 0.03 * fade * (sw / swe + 0.3);
      if (b > 0.02) continue;
      float v = q.y + t * sp, iy = floor(v / ch);
      for (float j = -1.0; j <= 1.0; j++) {
        float m = iy + j, hb = h1(m * 1.31 + n * 17.0);
        if (hb > 0.85) continue;
        // 每颗在格子里上下错一点、左右散开，不排成一串珠子
        float y = (m + 0.2 + 0.6 * h1(m * 2.7 + n)) * ch - t * sp;
        float up = clamp((0.92 - y) / 0.9, 0.0, 1.0);
        float rad = 0.0012 + 0.003 * hb * hb * hb + 0.0012 * up;
        vec2 c = vec2(src + sin(m * 2.1 + t * 6.0) * (0.0015 + 0.006 * up) + (h1(m + n) - 0.5) * (0.006 + 0.03 * up), y);
        vec4 bub = aqBubble(vec2(qx, q.y) - c, rad, b);
        col = col * (1.0 - bub.a * fade) + bc * bub.rgb * fade * (0.6 + 0.4 * h1(m * 5.0 + n));
      }
    }
  }
  // 大泡：少、慢，上下略扁，边缘一边浮一边轻轻晃
  if (sceneryOn(SCN_CARS) && uBeatRows < 0.5) {
    float bw = 0.13, bx = floor(qx / bw);
    for (float k = -1.0; k <= 1.0; k++) {
      float n = bx + k;
      float sp = 0.07 + 0.04 * h1(n + 41.0), ch = 0.42;
      float iy = floor((q.y + t * sp) / ch);
      for (float j = -1.0; j <= 0.0; j++) {
        float m = iy + j;
        if (h1(m * 3.7 + n * 11.0) > min(1.1 * uBubbles, 0.85)) continue;
        float rad = 0.008 + 0.016 * pow(h1(m + n * 5.0), 1.5);
        vec2 c = vec2((n + 0.2 + 0.6 * h1(m * 7.0 + n)) * bw + sin(t * 1.3 + m) * 0.02, (m + 0.5) * ch - t * sp);
        vec2 d = vec2(qx, q.y) - c;
        // 按方向缩放偏移，轮廓就晃：sin(3θ + φ)、sin(2θ - φ') 用方向的余弦正弦展开，不用 atan
        vec2 u = d / max(length(d), 1e-5);
        float c2 = u.x * u.x - u.y * u.y, s2 = 2.0 * u.x * u.y;
        float c3 = u.x * (4.0 * u.x * u.x - 3.0), s3 = u.y * (3.0 - 4.0 * u.y * u.y);
        float f1 = t * 4.0 + m, f2 = t * 3.0;
        float shape = 1.0 + 0.03 * (s3 * cos(f1) + c3 * sin(f1)) + 0.02 * (s2 * cos(f2) - c2 * sin(f2));
        vec4 bub = aqBubble(vec2(d.x, d.y * 1.18) / shape, rad, b);
        col = col * (1.0 - bub.a * fade) + bc * (bub.rgb + glowPt(d, rad * 1.4, b, 0.0) * 0.04) * fade;
      }
    }
  }
  // 跟拍冒的一排排大泡：一排六颗，冒出时从小涨到全大，先冲一下再慢慢浮；离这一排远的像素直接跳过。
  // 每颗带一圈光晕，雾后面糊开也是一排亮点，看得出跟拍
  for (int i = 0; i < 12; i++) {
    if (i >= uBurstN) break;
    vec4 B = uBurst[i];
    float a = t - B.y;
    if (B.z <= 0.0 || a < 0.0) continue;
    float y0 = 0.9 - 0.2 * a - 0.28 * (1.0 - exp(-a * 3.0));
    if (abs(q.y - y0) > 0.08 + b * 3.0) continue;
    float grow = smoothstep(0.0, 0.15, a) * (0.55 + 0.45 * B.z);
    for (float k = 0.0; k < 6.0; k++) {
      float h = h1(B.w + k * 7.3), h2 = h1(B.w * 1.7 + k * 3.1);
      float rad = (0.008 + 0.012 * h) * grow;
      vec2 c = vec2(B.x + (k - 2.5) * 0.05 + (h2 - 0.5) * 0.02 + sin(a * 3.0 + k * 1.7) * 0.004 * a, y0 + (h - 0.5) * 0.04);
      vec2 d = vec2(qx, q.y) - c;
      vec4 bub = aqBubble(vec2(d.x, d.y * 1.18), rad, b);
      col = col * (1.0 - bub.a * fade) + bc * (bub.rgb + glowPt(d, rad * 2.0 + 0.004, b, 0.0) * 0.22) * fade;
    }
  }
  return col;
}
`,m=`
// 每只：xy 位置（scene 坐标）、z 伞盖半径、w 收缩（0 舒张，1 收得最紧）；颜色 rgb（已按远近调暗），a 触手波的相位
uniform vec4 uJelly[24], uJellyC[24];
uniform int uJellyN;
vec3 jellies(vec2 q, float b) {
  vec3 c = vec3(0.0);
  float sharp = 1.0 - clamp(b * 25.0, 0.0, 1.0);
  bool fine = b < 0.004;
  for (int i = 0; i < 24; i++) {
    if (i >= uJellyN) break;
    vec4 J = uJelly[i];
    float size = J.z, pulse = J.w;
    vec2 d = q - J.xy;
    if (abs(d.x) > size * 2.2 + b * 3.0 || d.y < -size * 1.6 - b * 3.0 || d.y > size * 5.0 + b * 3.0) continue;
    vec3 jc = uJellyC[i].rgb * uNeon;
    float ph = uJellyC[i].a;
    // 伞盖：收缩时变窄变高；下沿往两边垂、带一圈波纹
    float wx = size * (1.0 - 0.18 * pulse), hy = size * 0.72 * (1.0 + 0.14 * pulse);
    vec2 p = vec2(d.x / wx, d.y / hy);
    float soft = b / size + 0.04;
    // 下沿那一段 r 涨得快（÷0.3），模糊时按 soft 放缓，不然雾后面伞裙下沿还是一道硬边；波纹也跟着糊掉
    float edge = 0.05 + 0.3 * p.x * p.x + 0.04 * cos(p.x * 14.0 + ph * 0.5) * sharp;
    float r = max(length(vec2(p.x, min(p.y, 0.0))), (p.y - edge) / (0.3 + soft) + 1.0);
    float fill = smoothstep(1.0 + soft, 1.0 - soft, r);
    float rr = (r - 0.9) / (0.09 + soft);
    float rim = exp(-rr * rr);
    vec2 pc = p + vec2(0.0, 0.4);
    float lobe = length(pc) - 0.26 - 0.07 * cos(4.0 * atan(pc.y, pc.x));
    float gon = exp(-lobe * lobe / (0.004 + soft * soft)) * (0.4 + 0.6 * sharp);
    c += jc * (fill * (0.16 + 0.55 * rim + 0.45 * gon) + glowPt(d + vec2(0.0, size * 0.25), size * 0.9, b, 0.0) * 0.18);
    if (d.y <= 0.0) continue;
    // 伞下一道拖尾光：模糊时靠它认出是水母
    float we = sqrt(pow(wx * 0.55, 2.0) + b * b);
    c += jc * exp(-d.x * d.x / (we * we)) * pow(max(1.0 - d.y / (size * 3.5), 0.0), 2.0) * 0.12 * smoothstep(0.0, size * 0.3, d.y);
    if (!fine) continue;
    // 触手：伞裙下沿垂下几根细丝，顺着往下传的波飘
    float w = 0.0012 + size * 0.012;
    we = sqrt(w * w + b * b);
    for (float k = 0.0; k < 6.0; k++) {
      float len = size * (2.6 + 1.6 * h1(float(i) * 13.0 + k));
      float s = d.y / len;
      if (s > 1.05) continue;
      float tx = (k / 5.0 - 0.5) * 1.5 * wx * (1.0 - 0.35 * min(s, 1.0));
      float dx = d.x - tx - sin(d.y / size * 2.6 - ph * 0.9 + k * 1.3) * size * 0.28 * min(s, 1.0);
      c += jc * (w / we) * exp(-dx * dx / (we * we)) * pow(max(1.0 - s, 0.0), 1.5) * smoothstep(hy * 0.1, hy * 0.35, d.y) * 0.8;
    }
    // 中间的口腕：粗一点、短一点的一条飘带
    float s = d.y / (size * 1.7);
    if (s < 1.0) {
      float wa = size * 0.14 * (1.0 - s * 0.6), wae = sqrt(wa * wa + b * b);
      float dx = d.x - sin(d.y / size * 3.5 - ph + 1.0) * size * 0.12 * s;
      c += jc * (wa / wae) * exp(-dx * dx / (wae * wae)) * (1.0 - s) * 0.35 * smoothstep(0.0, 0.01, d.y);
    }
  }
  return c;
}
`,h=`
float hash(vec2 p) { p = fract(p * vec2(123.34, 456.21)); p += dot(p, p + 45.32); return fract(p.x * p.y); }
// 格子编号 → 0..1 的伪随机数（Dave Hoskins 的 hash11）。原来用 fract(sin(n) × 43758)，sin 的精度各家驱动不一样、
// 编号随行驶越来越大时平台间会对不上；纯算术的分布一样（均匀、相邻编号不相关），只是每格具体的值换了
float h1(float n) { n = fract(n * 0.1031); n *= n + 33.33; n *= n + n; return fract(n); }
float vnoise(vec2 p) {
  vec2 i = floor(p), f = fract(p); f = f * f * (3.0 - 2.0 * f);
  return mix(mix(hash(i), hash(i + vec2(1, 0)), f.x), mix(hash(i + vec2(0, 1)), hash(i + vec2(1, 1)), f.x), f.y);
}
vec3 pick(float h) { return h < 0.34 ? uLa : (h < 0.67 ? uLb : uLc); }
// 高斯光点：r 光点半径，b 模糊半径，streak 横向拖影半长；模糊越大越暗、越散
float glowPt(vec2 d, float r, float b, float streak) {
  d.x = sign(d.x) * max(abs(d.x) - streak, 0.0);
  float s2 = r * r + b * b;
  return (r * r / s2) * exp(-dot(d, d) / s2 * 1.6);
}
// 软边矩形：size 半宽高，soft 模糊半径
float softBox(vec2 d, vec2 size, float soft) {
  vec2 q = abs(d) - size;
  float sd = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);
  float k = min(size.x, size.y);
  return (1.0 - smoothstep(-soft * 0.5, soft, sd)) * k / (k + soft * 0.6);
}
// 半径 r、模糊 b 的 glowPt 纵向照得到多远：再远指数项 < 1e-6，乘上最大的倍数（霓虹 2.8 × uNeon 2）也远不到 8 位输出的一级。
// 下面各组灯纵向照不到当前这一行就整组跳过，画面不变
float reach(float r, float b) { return 3.0 * sqrt(r * r + b * b); }
const float HZ = 0.6, ROAD = 0.7;
const float WIND = 0.6;   // 车速 1 时，雨相对车身每往下 1 就往后 0.6（和 JS 里的 WIND_DRIFT 对应）
const vec3 SODIUM = vec3(1.0, 0.68, 0.32);
// uScenery 的位：窗外画哪几样（设置里的开关，nebbiaFrameUniforms.ts）。聚光灯不占位，关掉时 uSpotI 为 0
const int SCN_BUILDINGS = 1, SCN_LIGHTS = 2, SCN_CARS = 4, SCN_ROAD = 8, SCN_RAIN = 16, SCN_RAIN_SLANT = 32;
bool sceneryOn(int bit) { return (uScenery & bit) != 0; }
`,g=`
// 聚光灯光柱：从楼底的光源射向玻璃上的光斑，越近越宽越亮；b 是模糊半径（雾后面看光柱更散）
float beamFrom(vec2 src, vec2 q, float b) {
  vec2 d = uSpotTgt - src;
  float L = length(d);
  if (uSpotI <= 0.0 || L < 1e-3) return 0.0;
  d /= L;
  vec2 rel = q - src;
  float along = dot(rel, d);
  // 到「光源—光斑」这条线段的距离，不是到无限长中轴线的：末端是以光斑为圆心的圆头。
  // 原来按中轴线算、再沿光柱方向平切，末端是一道和光柱一样宽的平口；两盏灯从不同角度打到同一处，
  // 两个平口的角露在光斑两侧，看着像个 X
  float perp = length(rel - d * clamp(along, 0.0, L));
  float k = clamp(along / L, 0.0, 1.0);
  float w = mix(0.004, uSpotR * 0.9, k) + b;
  float body = exp(-perp * perp / (w * w)) * smoothstep(-0.01, 0.04, along) * (0.25 + 0.75 * k * k);
  // 光源本身：楼底一颗很亮的灯
  float lamp = exp(-dot(rel, rel) / (0.00004 + b * b * 0.25)) * 2.5 / (1.0 + b * 50.0);
  return (body * 0.7 + lamp) * uSpotI;
}
// 左右两盏，光源左右对称，打到同一个字上
float spotBeam(vec2 q, float b) { return beamFrom(uSpotSrc, q, b) + beamFrom(uSpotSrc2, q, b); }
`,_=`${h}${g}${m}${p}
// 「舞潮踏拍」的水草一项（nebbiaWeedSway.ts）：弹簧现在、0.05、0.1、0.15 秒前的位置；根取现在、梢取 0.15 秒前，
// 从根往梢传
uniform vec4 uSway;
// 水草自己的摆动留多少：1 照常，舞潮踏拍的水草一项开着时 0（只跟拍走），切换时慢慢过渡（nebbiaAquarium.ts）
uniform float uWeedIdle;
float weedKick(float s) {
  float f = clamp(s, 0.0, 1.0) * 3.0;
  return f < 1.0 ? mix(uSway.x, uSway.y, f) : f < 2.0 ? mix(uSway.y, uSway.z, f - 1.0) : mix(uSway.z, uSway.w, f - 2.0);
}
vec3 scene(vec2 q, float b) {
  float t = uTime, sharp = 1.0 - clamp(b * 25.0, 0.0, 1.0);
  vec3 deep = uSky0 * 0.55;
  vec3 col = mix(mix(uSky1, uLa, 0.3) * 1.25 + 0.02, deep, smoothstep(0.0, 0.95, q.y));
  vec3 lightC = mix(uLa, vec3(1.0), 0.45);
  // 水面照下来的光束，越往下越淡；最上面一层水面的粼光
  float rx = q.x + q.y * 0.35;
  float rays = pow(0.5 + 0.5 * sin(rx * 9.0 + t * 0.3 + sin(rx * 3.1 - t * 0.2) * 1.5), 6.0)
             + 0.6 * pow(0.5 + 0.5 * sin(rx * 17.0 - t * 0.45), 8.0);
  col += lightC * mix(0.25, rays, sharp * 0.6 + 0.4) * (1.0 - smoothstep(0.0, 0.85, q.y)) * 0.13;
  col += lightC * 0.8 * exp(-q.y * 14.0) * (0.25 + 0.15 * sin(q.x * 30.0 + t * 1.3 + sin(q.x * 7.0)) * sharp);
  // 沙底和焦散（「马路」）
  if (sceneryOn(SCN_ROAD)) {
    float floorY = 0.89 + 0.015 * sin(q.x * 3.0);
    float caus = pow(abs(sin(q.x * 23.0 + sin(q.y * 40.0 + t) * 1.2 + t * 0.6) * sin(q.x * 17.0 - t * 0.5 + q.y * 30.0)), 3.0);
    col = mix(col, deep * 1.3 + 0.03 + lightC * caus * 0.12 * sharp, smoothstep(floorY - 0.01 - b, floorY + 0.01 + b, q.y));
  }
  // 礁石（「楼群」）：顶上被水面的光照亮、带一点焦散，往下渐暗，表面有坑洼的纹理；比后面的水亮一点才看得出是石头
  if (sceneryOn(SCN_BUILDINGS)) {
    float rockY = 0.8 + 0.07 * vnoise(vec2(q.x * 3.5, 1.0)) + 0.02 * vnoise(vec2(q.x * 14.0, 3.0));
    float under = max(q.y - rockY, 0.0);
    float tex = 0.75 + 0.5 * mix(0.5, vnoise(q * vec2(26.0, 34.0)) * 0.65 + vnoise(q * vec2(70.0, 90.0)) * 0.35, sharp);
    vec3 rock = mix(deep * 1.6 + lightC * 0.07, deep * 0.75, smoothstep(0.0, 0.09, under)) * tex;
    float rc = pow(abs(sin(q.x * 31.0 + t * 0.5 + sin(q.y * 50.0 - t * 0.7)) * sin(q.x * 19.0 - t * 0.4 + q.y * 37.0)), 4.0);
    rock += lightC * (0.16 * exp(-under * 70.0) + rc * 0.07 * sharp * (1.0 - smoothstep(0.0, 0.05, under)));
    col = mix(col, rock, smoothstep(rockY - b - 0.003, rockY + b + 0.003, q.y) * 0.95);
    // 石脊前面一块块圆石头：按椭球的法线算受光（光从水面往下打），顶上亮、底下暗，边缘被噪声啃得不圆
    float rw = 0.17, rcx = floor(q.x / rw);
    for (float k = -1.0; k <= 1.0; k++) {
      float n = rcx + k, h = h1(n * 4.1 + 2.0);
      if (h > 0.7) continue;
      float rx = 0.06 + 0.05 * h1(n + 9.0), ry = rx * (0.55 + 0.3 * h1(n * 2.7));
      vec2 c = vec2((n + 0.3 + 0.4 * h1(n * 5.3)) * rw, 0.95);
      vec2 d = (q - c) / vec2(rx, ry);
      float edge = 0.12 * (vnoise(vec2(atan(d.y, d.x) * 2.5, n * 3.0)) - 0.5);
      float e = length(d) + edge, soft = (b + 0.003) / ry;
      float cover = smoothstep(1.0 + soft, 1.0 - soft, e);
      if (cover <= 0.0) continue;
      vec3 nrm = normalize(vec3(d * 0.9, sqrt(max(1.0 - dot(d, d), 0.04))));
      float lit = max(dot(nrm, normalize(vec3(-0.35, -1.0, 0.7))), 0.0);
      vec3 stone = (deep * 1.8 + lightC * 0.08) * (0.4 + 1.0 * lit) * tex + lightC * pow(lit, 6.0) * 0.16 * sharp;
      col = mix(col, stone, cover * 0.97);
    }
  }
  // 水草（「楼群」）和长在上面的一串串荧光粒子（「光斑」）：水草从根到梢传一道波，摆得快一点，
  // 「舞潮踏拍」时再跟拍往一边推（weedKick）；粒子跟着草身一起摆，越往梢越亮，各自一闪一闪
  bool weeds = sceneryOn(SCN_BUILDINGS), glow = sceneryOn(SCN_LIGHTS);
  if (weeds || glow) {
    float cw = 0.06, cx = floor(q.x / cw);
    for (float k = -1.0; k <= 1.0; k++) {
      float n = cx + k, h = h1(n * 1.7 + 5.0);
      if (h > 0.45) continue;
      float top = 0.55 + 0.25 * h1(n + 2.0), base = 0.92, len = base - top;
      float sway = t * (3.6 + 1.8 * h) + h * 20.0, bend = 0.07 * (0.7 + 0.6 * h);
      if (weeds) {
        float s = clamp((base - q.y) / len, 0.0, 1.0);
        float x0 = (n + 0.5) * cw + sin(q.y * 11.0 + sway) * 0.025 * uWeedIdle * s + weedKick(s) * bend * s * s;
        float w = 0.003 * (1.0 - s * 0.6), we = sqrt(w * w + b * b), dx = q.x - x0;
        float a = (w / we) * exp(-dx * dx / (we * we)) * smoothstep(top - b, top + 0.02 + b, q.y) * step(q.y, base) * 0.5;
        col = mix(col, mix(deep, vec3(0.02, 0.08, 0.05), 0.6), a);
      }
      // 草梢上面离粒子太远（雾的预渲染要算上光晕糊开的范围）就跳过
      if (!glow || q.y < top - 0.05 - b * 3.0) continue;
      float s = clamp((base - q.y) / len, 0.0, 1.0);
      float N = 11.0, gap = len / (N - 1.0);
      // 雾后面（预渲染）粒子糊成一条：按连续的一条光带算，不一颗颗算，免得光晕比间距宽时漏算出断层
      if (b > 0.012) {
        float xs = (n + 0.5) * cw + sin(q.y * 11.0 + sway) * 0.025 * uWeedIdle * s + weedKick(s) * bend * s * s, dx = q.x - xs;
        float fj = s * (N - 1.0), ja = floor(fj);
        vec3 pc = mix(mix(pick(h1(n * 3.0 + ja)), pick(h1(n * 3.0 + ja + 1.0)), fj - ja), vec3(1.0), 0.25);
        float line = 0.0;
        for (float r3 = 0.0; r3 < 3.0; r3++) {
          float r = r3 < 0.5 ? 0.0018 + 0.0018 * s : r3 < 1.5 ? (0.0018 + 0.0018 * s) * 4.0 : 0.009 + 0.008 * s;
          float wgt = r3 < 0.5 ? 1.6 : r3 < 1.5 ? 0.25 : 0.4;
          // 一颗 glowPt 沿竖直方向积分，再除以间距：一串粒子的平均亮度
          float s2 = r * r + b * b;
          line += wgt * (r * r / s2) * exp(-dx * dx / s2 * 1.6) * sqrt(3.1416 * s2 / 1.6) / gap;
        }
        float e = 0.009 + b;
        line *= smoothstep(top - e, top + e, q.y) * smoothstep(base + e, base - e, q.y);
        col += pc * line * 0.7 * (0.35 + 0.65 * s) * uNeon;
        continue;
      }
      // 清晰的画面一颗颗算：最近的一颗前后各两颗（光晕比间距宽），超出两头的不算，不会把梢上那颗算两遍
      float j0 = floor(s * (N - 1.0) + 0.5);
      for (float dj = -2.0; dj <= 2.0; dj++) {
        float j = j0 + dj, sj = j / (N - 1.0);
        if (j < 0.0 || j > N - 1.0) continue;
        float py = base - sj * len;
        vec2 pp = vec2((n + 0.5) * cw + sin(py * 11.0 + sway) * 0.025 * uWeedIdle * sj + weedKick(sj) * bend * sj * sj, py);
        float tw = 0.45 + 0.55 * pow(0.5 + 0.5 * sin(t * (2.5 + h1(n * 9.0 + j) * 3.0) + j * 1.7 + h * 30.0), 2.0);
        float pr = 0.0018 + 0.0018 * sj;
        vec3 pc = mix(pick(h1(n * 3.0 + j)), vec3(1.0), 0.25);
        // 每颗带一圈大光晕：雾后面细小的亮点糊没了，光晕糊开还是一团团彩色的光
        vec2 d = q - pp;
        col += pc * (glowPt(d, pr, b, 0.0) * 1.6 + glowPt(d, pr * 4.0, b, 0.0) * 0.25 + glowPt(d, 0.009 + 0.008 * sj, b, 0.0) * 0.4) * tw * (0.35 + 0.65 * sj) * uNeon;
      }
    }
  }
  col += jellies(q, b);
  col = aqBubbles(col, q, b, mix(lightC, vec3(1.0), 0.4));
  // 聚光灯：从水面上方打到正在写的字上
  col += lightC * spotBeam(q, b) * 0.8;
  return col;
}
`,v=`${h}// 路灯和招牌（倒影也用它）
vec3 lampsSigns(vec2 q, float b, float t) {
  vec3 c = vec3(0.0);
  // 招牌、店面：近，移动快，带横向拖影
  float xs = q.x + t * 0.42, cw = 0.34, streak = 0.012 * uBus;
  float cs = floor(xs / cw);
  // 招牌的灯在 0.46–0.56 高、店面在 0.645（倒影调用时 q、b 不一样，各自判断）
  bool sg = abs(q.y - 0.51) < 0.05 + reach(0.016, b + 0.002), sh = abs(q.y - 0.645) < reach(0.026, b + 0.003);
  if (sg || sh) for (float k = -1.0; k <= 1.0; k++) {
    float n = cs + k;
    // 招牌、店面的灯：都画成圆的散景光斑（远处的灯失焦后是圆的），不画方块
    if (sg) {
      vec2 cen = vec2((n + 0.2 + 0.6 * h1(n)) * cw, 0.46 + 0.1 * h1(n + 17.0));
      if (h1(n + 33.0) < 0.75) c += pick(h1(n + 2.0)) * glowPt(vec2(xs, q.y) - cen, 0.006 + 0.01 * h1(n + 5.0), b + 0.002, streak) * 1.6;
    }
    // 店面：底层一两团暖光
    vec2 shop = vec2((n + 0.5) * cw, 0.645);
    if (sh && h1(n + 41.0) < 0.6) c += mix(SODIUM, pick(h1(n + 8.0)), 0.3) * glowPt(vec2(xs, q.y) - shop, 0.016 + 0.01 * h1(n + 3.0), b + 0.003, streak) * 0.9;
  }
  // 路灯：中景，灯头 + 雨里的大光晕（0.36 高，光晕半径 0.07）
  float xl = q.x + t * 0.22, lw = 0.55;
  float cl = floor(xl / lw);
  if (abs(q.y - 0.36) < reach(0.07, b)) for (float k = -1.0; k <= 1.0; k++) {
    vec2 d = vec2(xl - (cl + k + 0.5) * lw, q.y - 0.36);
    c += SODIUM * (glowPt(d, 0.007, b, 0.004 * uBus) * 2.6 + glowPt(d, 0.07, b, 0.0) * 0.3);
  }
  return c;
}
// 一层楼群（layer 0 = 远，1 = 近）。返回 rgb = 颜色，a = 覆盖度。
// 每格一栋：宽度、高度随机，一半带收分的顶层，少数顶上有天线；立面下亮上暗（被街灯照）、一侧有一道边光；
// 窗是小格子，每栋亮灯比例不同，有的整层全黑，亮着的窗分暖白、冷白和主题色，亮度不一。
vec4 building(vec2 q, float b, float t, float layer) {
  float near = layer;
  float x = q.x + t * mix(0.02, 0.035, near) + layer * 7.7;
  float cw = mix(0.065, 0.12, near), aa = max(b, 0.0012);
  float c0 = floor(x / cw);
  vec4 res = vec4(0.0);
  // 每栋楼（连收分顶层、天线）都落在自己那一格里，左右邻居只在离格边不到模糊半径（再加天线宽）时才碰得到这个像素：
  // 离得更远时邻居的覆盖度必然是 0，跳过，画面不变。清晰窗外模糊半径很小，大部分像素只用算本格一栋
  float fx = x - c0 * cw, nbReach = aa + 0.0013;
  for (float k = -1.0; k <= 1.0; k++) {
    if ((k < 0.0 && fx > nbReach) || (k > 0.0 && cw - fx > nbReach)) continue;
    float n = c0 + k + layer * 100.0;
    float w = cw * (0.72 + 0.26 * h1(n));
    float left = (c0 + k) * cw + (cw - w) * h1(n + 1.0);
    float lx = x - left;
    float hgt = mix(0.07, 0.11, near) + mix(0.16, 0.27, near) * pow(h1(n + 2.0), 1.5);
    float base = HZ + near * 0.015;
    float top = base - hgt;
    // 收分的顶层
    bool tier = h1(n + 3.0) < 0.5;
    float tw = w * (0.5 + 0.25 * h1(n + 4.0)), tl = (w - tw) * 0.5, th = hgt * (0.12 + 0.15 * h1(n + 5.0));
    float body = smoothstep(-aa, aa, lx) * smoothstep(-aa, aa, w - lx) * smoothstep(-aa, aa, q.y - top);
    float up = tier ? smoothstep(-aa, aa, lx - tl) * smoothstep(-aa, aa, tl + tw - lx) * smoothstep(-aa, aa, q.y - (top - th)) : 0.0;
    float roof = tier ? top - th : top;
    // 天线
    float ant = h1(n + 6.0) < 0.3 ? smoothstep(0.0012 + aa, 0.0, abs(lx - w * 0.5)) * smoothstep(-aa, aa, q.y - (roof - 0.04 - 0.03 * h1(n + 7.0))) : 0.0;
    float cov = max(max(body, up), ant);
    if (cov <= 0.0) continue;
    // 立面：暗色，按楼略有差别；越往下越被街面的灯照亮；左侧一道细边光
    vec3 fac = mix(uSky0, vec3(0.015, 0.018, 0.025), 0.55) * (0.7 + 0.6 * h1(n + 8.0));
    float lowLit = pow(clamp((q.y - roof) / (base - roof), 0.0, 1.0), 2.5);
    fac += mix(SODIUM, uLa, 0.5) * 0.1 * lowLit;
    fac += mix(uSky1, uLb, 0.3) * 0.12 * smoothstep(0.003 + aa, 0.0, lx) * body;
    // 窗格
    float fh = mix(0.0075, 0.012, near), wc = mix(0.0055, 0.0085, near);
    vec2 g = vec2((lx - wc * 0.6) / wc, (q.y - roof - fh * 0.8) / fh);
    vec2 id = floor(g), f = fract(g);
    float dens = 0.15 + 0.5 * h1(n + 9.0);
    float floorOn = step(0.18, hash(vec2(id.y, n)));
    float on = step(hash(id + n * 3.7), dens) * floorOn;
    // 窗区边界跟着模糊半径变软，糊开时不出硬边
    float inside = smoothstep(-aa, aa, lx - wc * 0.6) * smoothstep(-aa, aa, w - wc * 0.6 - lx) * smoothstep(-aa, aa, q.y - roof - fh * 0.8) * smoothstep(-aa, aa, base - fh - q.y);
    float pane = smoothstep(0.32, 0.22, abs(f.x - 0.5)) * smoothstep(0.3, 0.2, abs(f.y - 0.5));
    // 被雾糊开时窗格变成平均亮度，不闪
    float blurMix = clamp(b / (wc * 0.8), 0.0, 1.0);
    float win = mix(pane * on, 0.3 * dens * 0.82, blurMix) * inside * max(body, up);
    float hc = hash(id + n * 1.3);
    vec3 wcol = hc < 0.5 ? vec3(1.0, 0.78, 0.5) : (hc < 0.8 ? vec3(0.78, 0.88, 1.0) : pick(hash(id + 5.0)));
    // 糊开时每格的颜色、亮度也换成平均值，否则会出现马赛克
    wcol = mix(wcol * (0.55 + 0.6 * hash(id + 2.9)), vec3(0.95, 0.82, 0.66) * 0.85, blurMix);
    vec3 bc = fac + wcol * win * mix(0.7, 1.0, near);
    // 远层被大气冲淡，往天色靠
    bc = mix(bc, mix(uSky0, uSky1, 0.7), mix(0.45, 0.12, near));
    res = vec4(mix(res.rgb, bc, cov), max(res.a, cov));
  }
  return res;
}
// 从楼群缓存取一层（预乘颜色）：横向按这层的速度（building 里的 0.02、0.035）平移，环形取样
vec4 bldAt(sampler2D s, vec2 q, float t, float speed, vec3 g) {
  // 没有 mipmap；在分支里取样，用 textureLod 不依赖导数
  return textureLod(s, vec2((q.x + t * speed) / g.z, (q.y - g.x) / (g.y - g.x)), 0.0);
}
// 主题色霓虹（照雨湖的远处灯光）：楼群之间密密的彩色小散景，偶尔一大团霓虹带一圈很大的光晕。
// 颜色取这首歌的三个灯光色；移动速度介于楼群和路灯之间。倒影也用它。
vec3 neonLights(vec2 q, float b, float t) {
  vec3 c = vec3(0.0);
  float bb = b + 0.003;   // 散景本身就是虚的
  // 小散景：两排格子，每格最多一颗，落在楼群那一段高度里（0.38–0.58）
  if (abs(q.y - (HZ - 0.12)) < 0.1 + reach(0.012, bb)) for (float row = 0.0; row < 2.0; row++) {
    float x = q.x + t * (0.06 + row * 0.03) + row * 7.3, cw = 0.045 - row * 0.012;
    float c0 = floor(x / cw);
    for (float k = -1.0; k <= 1.0; k++) {
      float n = c0 + k + row * 300.0;
      if (h1(n + 11.0) > 0.85) continue;
      vec2 cen = vec2((c0 + k + 0.15 + 0.7 * h1(n)) * cw, HZ - 0.02 - 0.2 * pow(h1(n + 3.0), 1.6));
      float r = 0.004 + 0.008 * pow(h1(n + 6.0), 3.0);
      c += pick(h1(n + 9.0)) * glowPt(vec2(x, q.y) - cen, r, bb, 0.0) * (1.0 + 1.8 * h1(n + 13.0));
    }
  }
  // 大霓虹：稀疏，一个亮核 + 一圈很大的同色光晕（雨湖左边那盏洋红灯）
  float xb = q.x + t * 0.1, bw = 0.75, cb = floor(xb / bw);
  if (abs(q.y - (HZ - 0.14)) < 0.06 + reach(0.13, b)) for (float k = -1.0; k <= 1.0; k++) {
    float n = cb + k + 500.0;
    vec2 d = vec2(xb - (cb + k + 0.2 + 0.6 * h1(n)) * bw, q.y - (HZ - 0.08 - 0.12 * h1(n + 2.0)));
    vec3 lc = pick(h1(n + 4.0));
    c += lc * (glowPt(d, 0.014, bb, 0.0) * 2.0 + glowPt(d, 0.13, b, 0.0) * 0.4);
  }
  return c;
}
${g}
// 公交车侧窗外的雨夜街景：远处楼群慢、路灯中速、招牌和车灯快
vec3 scene(vec2 q, float b) {
  float t = uTime * uBus;
  vec3 col = mix(uSky0, uSky1, smoothstep(0.0, HZ, q.y));
  col += mix(uLa, SODIUM, 0.4) * 0.14 * exp(-abs(q.y - HZ + 0.12) * 6.0);
  // 楼群：远近两层，远层矮、淡、窗小；近层高、细节多
  float aa = max(b, 0.0015), c0;
  // 楼群走缓存（nebbiaBuildingCache.ts）。水珠透镜（b = 0.006）折射得远，会取到缓存范围外面，照旧现算（只占几颗水珠的面积）；
  // 缓存还没画好的那档也现算
  bool bldOn = sceneryOn(SCN_BUILDINGS);
  bool cached = b < 0.002 ? uBldOn.x : (b > 0.05 ? uBldOn.z : (b > 0.02 ? uBldOn.y : false));
  if (bldOn && cached) {
    vec4 bf, bn;
    if (b > 0.05) { bf = bldAt(uBld4, q, t, 0.02, uBldGeo[2]); bn = bldAt(uBld5, q, t, 0.035, uBldGeo[2]); }
    else if (b > 0.02) { bf = bldAt(uBld2, q, t, 0.02, uBldGeo[1]); bn = bldAt(uBld3, q, t, 0.035, uBldGeo[1]); }
    else { bf = bldAt(uBld0, q, t, 0.02, uBldGeo[0]); bn = bldAt(uBld1, q, t, 0.035, uBldGeo[0]); }
    col = col * (1.0 - bf.a) + bf.rgb;
    col = col * (1.0 - bn.a) + bn.rgb;
  } else if (bldOn) {
    vec4 bf = building(q, b, t, 0.0);
    col = mix(col, bf.rgb, bf.a);
    vec4 bn = building(q, b, t, 1.0);
    col = mix(col, bn.rgb, bn.a);
  }
  // 主题色霓虹压在楼群前面；天边再铺一层很宽的主题色辉光
  bool lightsOn = sceneryOn(SCN_LIGHTS);
  if (lightsOn) {
    col += (neonLights(q, b, t) + uLa * 0.12 * exp(-abs(q.y - HZ + 0.1) * 5.0)) * uNeon;
    col += lampsSigns(q, b, t);
  }
  // 湿路面和倒影（路面关掉时车灯也没有倒影）
  float road = sceneryOn(SCN_ROAD) ? smoothstep(ROAD - aa, ROAD + aa, q.y) : 0.0;
  col = mix(col, uSky0 * 0.5, road * 0.85);
  // 倒影都乘 road，路面以上恰好是 0：直接跳过，画面不变，省掉屏上大半像素的两整套招牌、霓虹计算
  if (road > 0.0 && lightsOn) {
    vec2 qr = vec2(q.x, ROAD - (q.y - ROAD) * 0.55);
    col += lampsSigns(qr, b + 0.012, t) * road * 0.35;
    // 霓虹倒影：纵向压得更扁，所以在湿路面上拉成竖直的光柱（雨湖的倒影做法）
    vec2 qn = vec2(q.x, HZ - (q.y - ROAD) * 0.22);
    col += neonLights(qn, b + 0.008, t) * road * 0.45 * uNeon;
  }
  // 车灯：同向车道尾灯（红），对向车道前灯（白），快速掠过拖出长影
  if (sceneryOn(SCN_CARS)) for (float lane = 0.0; lane < 2.0; lane++) {
    float sp = lane < 0.5 ? 0.55 : -1.1, ly = lane < 0.5 ? 0.76 : 0.83, ww = 0.9;
    float xc = q.x + t * sp, cc = floor(xc / ww);
    vec3 lc = lane < 0.5 ? vec3(1.0, 0.18, 0.12) : vec3(1.0, 0.95, 0.85);
    // 车灯和它在路面上的倒影纵向都照不到这一行（倒影乘 road，路面以上本来就是 0）就跳过这条车道
    bool hd = abs(q.y - ly) < reach(0.004, b), rf = road > 0.0 && abs((q.y - ly) * 3.0 - 0.03) < reach(0.02, b * 2.0);
    if (!hd && !rf) continue;
    for (float k = -1.0; k <= 1.0; k++) {
      float n = cc + k + lane * 50.0;
      if (h1(n) > 0.6) continue;
      float cx = (cc + k + 0.2 + 0.6 * h1(n + 4.0)) * ww;
      float st = abs(sp) * 0.03 * uBus;
      if (hd) {
        c0 = glowPt(vec2(xc - cx + 0.035, q.y - ly), 0.004, b, st) + glowPt(vec2(xc - cx - 0.035, q.y - ly), 0.004, b, st);
        col += lc * c0 * 2.2;
      }
      if (rf) col += lc * glowPt(vec2(xc - cx, (q.y - ly) * 3.0 - 0.03), 0.02, b * 2.0, st) * road * 0.3;
    }
  }
  // 窗外的雨：相对车身的速度 = 往下落 + 往后（车往右开，窗外往左走），车越快越斜；停站时竖直落。
  // 雨丝只在聚光灯光柱里画（被照亮的那些）。设置里可以让它总是竖直落，或者不下（只剩光柱）
  float beam = spotBeam(q, b);
  if (!sceneryOn(SCN_RAIN)) return col + uLa * beam;
  vec2 rd = normalize(vec2(sceneryOn(SCN_RAIN_SLANT) ? -WIND * uBus : 0.0, 1.0));
  vec2 r = vec2(dot(q, rd), dot(q, vec2(-rd.y, rd.x)));
  float colId = floor(r.y / 0.007), hr = h1(colId);
  float seg = fract(r.x * (1.5 + hr) - uTime * (2.5 + hr * 2.0) * (1.0 + 0.4 * uBus) + hr * 10.0);
  float line = smoothstep(0.0012 + b, 0.0, abs(fract(r.y / 0.007) - 0.5) * 0.007) * smoothstep(0.0, 0.1, seg) * smoothstep(0.3, 0.1, seg);
  // 聚光灯：光柱叠在最前面；光柱里的雨丝被照亮
  col += uLa * beam + mix(uLa, vec3(1.0), 0.5) * line * beam * 1.6 * step(0.3, hr) * (1.0 - clamp(b * 40.0, 0.0, 1.0));
  return col;
}
`,y=`#version 300 es
in vec2 aPos; out vec2 vUv;
void main() { vUv = aPos * 0.5 + 0.5; vUv.y = 1.0 - vUv.y; gl_Position = vec4(aPos, 0.0, 1.0); }`,b=e=>`precision highp float;
uniform vec2 uRes, uSceneShift; uniform float uTime, uBus, uNeon;
uniform vec3 uSky0, uSky1, uLa, uLb, uLc;
// 聚光灯：光源（远处楼底）、照到玻璃上的点、光斑半径，都在 scene 坐标；uSpotI 强度
uniform vec2 uSpotSrc, uSpotSrc2, uSpotTgt; uniform float uSpotR, uSpotI;
// 窗外画哪几样（位见 nebbiaSceneGlsl.ts 的 SCN_*）
uniform int uScenery;
// 楼群缓存（nebbiaBuildingCache.ts）：三档模糊 × 远近两层，预乘颜色；uBldOn 每档画好了没有，uBldGeo 每档 (y0, y1, 横向周期)
uniform bvec3 uBldOn; uniform sampler2D uBld0, uBld1, uBld2, uBld3, uBld4, uBld5; uniform vec3 uBldGeo[3];
in vec2 vUv; layout(location = 0) out vec4 outColor;
${e}`,x=b(v),S=b(_),C=e=>`#version 300 es
${e}
uniform float uFogB;
void main() {
  vec2 s0 = vUv + uSceneShift, q0 = vec2(s0.x * uRes.x / uRes.y, s0.y);
  outColor = vec4(scene(q0, uFogB), 1.0);
}`,w=C(x),T=`#version 300 es
${x}
uniform float uFillB, uFillLayer, uFillX0; uniform vec3 uFillGeo; uniform vec2 uFillSize;
void main() {
  float span = uFillGeo.z;
  float xw = uFillX0 + fract((gl_FragCoord.x / uFillSize.x * span - uFillX0) / span) * span;
  vec4 r = building(vec2(xw, mix(uFillGeo.x, uFillGeo.y, gl_FragCoord.y / uFillSize.y)), uFillB, 0.0, uFillLayer);
  outColor = vec4(r.rgb * r.a, r.a);
}`,E=`
vec3 staticNoise(vec2 px) { return vec3(vnoise(px / 6.0) * 0.6 + vnoise(px / 2.2) * 0.4, vnoise(px / 55.0 + 3.1), vnoise(px / 1.6)); }`,D=`#version 300 es
${x}
${E}
void main() { outColor = vec4(staticNoise(gl_FragCoord.xy), 1.0); }`,O=(e,t=x,n=!1)=>`#version 300 es
#define NEBBIA_FOG_TEX ${+!!e}
${n?`#define NEBBIA_DEW 1
`:``}${t===S?`#define NEBBIA_SOFT_HIGHLIGHT 1
`:``}${t}
uniform sampler2D uMask, uDrop, uField;
#ifdef NEBBIA_DEW
// 凝露玻璃上冷凝水珠的高光（tuning.dewGlint）；细水珠折射的窗外（nebbiaFogGl.ts 预渲染的清晰的那层）
uniform float uDewGlint;
uniform sampler2D uFogSharp;
#endif
// 哈气的白气遮罩（R 形状、G 浓度，nebbiaBreath.ts）；屏上没有哈气时 uBreathOn 为假，不采样
uniform sampler2D uBreath; uniform bool uBreathOn; uniform float uBreathThick;
uniform vec2 uGlassShift; uniform float uMist, uEdge, uRefract, uGlint;
// 预渲染好的两层雾（NEBBIA_FOG_TEX 为 0 时主画面自己算：不支持浮点渲染目标时）
uniform sampler2D uFogBlur, uFogGlow;
// 静态噪声纹理（nebbiaNoiseTex.ts）：按 px 取样；uNoiseOn 为假（还没算好、画布超过纹理尺寸上限）时现算
uniform bool uNoiseOn; uniform sampler2D uNoise;
// 透明（窗外是共用背景或桌面）：不画窗外，输出预乘的透明度。雾是一层半透明的雾，擦开的地方全透明，
// 光（聚光灯、擦痕边、水珠高光）只加颜色不加不透明度，叠在后面的东西上。雾的颜色跟主题日夜：日间乳白、夜间深色
uniform bool uSeeThrough; uniform vec3 uMistTint;
${E}
void main() {
  // 颠簸：玻璃上的东西（雾、擦痕、水珠）跟车身一起晃，窗外晃得更多
  vec2 gv = vUv + uGlassShift, sv = vUv + uSceneShift;
  vec2 px = gv * uRes;
  float aspect = uRes.x / uRes.y;
  vec2 q = vec2(sv.x * aspect, sv.y);
  // 擦痕边缘：细噪声让边缘毛糙，大块噪声让重新起雾时一片片回来
  vec3 nz = uNoiseOn ? texture(uNoise, px / uRes).rgb : staticNoise(px);
  float fine = nz.r, blotch = nz.g;
  // 哈气：就是这一片的雾一下变密（更细密的一层水汽凝上来），不是另盖一层。边由大块噪声揉散成一卷卷的、
  // 细噪声让它发毛，散的时候从薄的地方先透。在白气上写的字照样擦开，水珠周围照样抹开一圈
  float breath = 0.0, melt = 0.0;
  if (uBreathOn) {
    vec3 bm = texture(uBreath, gv).rgb;
    if (bm.r > 0.003) {
      // 值噪声直接取阈值会露出横平竖直的格子：每层转一个角度、叠三层，边缘才是一卷卷的
      vec2 r1 = mat2(0.80, -0.60, 0.60, 0.80) * px, r2 = mat2(0.36, 0.93, -0.93, 0.36) * px;
      float bn = vnoise(r1 / 34.0 + 7.0) * 0.55 + vnoise(r2 / 15.0 + 2.3) * 0.3 + vnoise(px / 6.5) * 0.15;
      float bth = 0.3 + (bn - 0.5) * 0.5;
      float cover = smoothstep(bth - 0.2, bth + 0.25, bm.r);
      breath = cover * bm.g * uBreathThick;
      // 白气漫到哪，那儿被盖住的旧字跟着糊开（B 通道，只在鼓开的那一阵）
      melt = cover * bm.b;
    }
  }
  float m = texture(uMask, gv).r;
  // 被白气盖住的旧字：遮罩在周围一圈取几下（每个像素转一个角度，糊出来是颗粒的雾气，不是几道重影），字先糊开、边变软，
  // 再随 yieldFactor 淡进雾里
  if (melt > 0.01) {
    float rad = melt * 0.014 * uRes.y, a0 = hash(px) * 6.2832, acc = 0.0;
    for (int i = 0; i < 8; i++) {
      float a = a0 + float(i) * 0.7854, rr = rad * ((i & 1) == 0 ? 1.0 : 0.5);
      acc += texture(uMask, gv + vec2(cos(a), sin(a)) * rr / uRes).r;
    }
    m = mix(m, acc / 8.0, melt);
  }
  float th = 0.3 + (fine - 0.5) * 0.22 * uEdge + (blotch - 0.5) * 0.12;
  float clear = smoothstep(th - 0.05 - 0.12 * melt, th + 0.06 + 0.06 * melt, m);
  // 白气铺开后，盖掉的旧字在遮罩上留着 BREATH_GHOST（nebbiaScene.ts）那么一点：比阈值低、不透出窗外，
  // 只让那儿的白气薄一点，糊糊的留个浅浅的印子。在周围四下取样糊开；新写的字在遮罩上是满的，取到的有一下超过 0.15
  // 就是挨着新字，不薄（不然新字外面也薄一圈）
  if (breath > 0.001) {
    float gr = 0.006 * uRes.y, ga = hash(px + 17.0) * 6.2832, gm = 0.0, gx = m;
    for (int i = 0; i < 4; i++) {
      float a = ga + float(i) * 1.5708, g = texture(uMask, gv + vec2(cos(a), sin(a)) * gr / uRes).r;
      gm += g; gx = max(gx, g);
    }
    breath *= 1.0 - 0.55 * smoothstep(0.0, 0.07, gm * 0.25) * (1.0 - smoothstep(0.1, 0.15, gx));
  }
  // 雾：模糊的窗外提亮发灰，光晕散得更开，加浓淡起伏和细颗粒，下半部分略浓
  float dens = uMist * (0.82 + 0.3 * vnoise(px / 380.0 + uTime * 0.015)) * (0.9 + 0.2 * vUv.y) + breath * 0.9;
  vec3 haze = vec3(0.70, 0.74, 0.78);
  vec3 blur = vec3(0.0), glow = vec3(0.0);
  if (!uSeeThrough) {
#if NEBBIA_FOG_TEX
  // 预渲染纹理的第 0 行是 vUv.y = 1（屏幕底部），所以纵向翻一下
  vec2 fu = vec2(vUv.x, 1.0 - vUv.y);
  blur = texture(uFogBlur, fu).rgb;
  glow = texture(uFogGlow, fu).rgb;
#else
  blur = scene(q, 0.03);
  glow = scene(q, 0.09);
#endif
  }
  // 密到普通雾的范围以上（哈气）时，窗外再暗一点、乳白再多一点
  vec3 fog = blur * max(0.08, 1.0 - 0.45 * dens - 0.25 * breath) + glow * 0.9 * dens + haze * (0.2 * dens + 0.25 * breath);
  // 透明时雾的浓淡就是不透明度，颜色是 uMistTint
  float alpha = 1.0;
  if (uSeeThrough) {
    // 深色的雾叠在暗桌面上不显，比白雾浓一点
    alpha = clamp((0.62 * dens + 0.3 * breath) * mix(1.3, 1.0, dot(uMistTint, vec3(0.333))), 0.0, 0.92);
    fog = uMistTint * alpha;
  }
  // 透明时颗粒乘不透明度：雾很淡的地方不在后面的东西上撒亮点
  fog += (hash(px) - 0.5) * 0.025 * alpha;
  // 聚光灯照到玻璃上：雾里的小水滴把光散开，字周围一圈雾被照成强调色
  vec2 sd = q - uSpotTgt;
  fog += uLa * uSpotI * exp(-dot(sd, sd) / (uSpotR * uSpotR * 2.2)) * (0.18 + 0.4 * dens);
  fog += smoothstep(0.78, 0.98, nz.b) * 0.05 * dens;
  // 水珠纹理：擦痕边 / 下淌的水珠 rd，雾上的凝结水珠 fd（擦开的地方没有）
  vec4 rd = texture(uDrop, gv);
  vec4 fd = texture(uField, gv) * (1.0 - clear);
  // 和 raindrop-fx 一样，水珠周围一圈的雾也被抹掉
  clear = max(clear, smoothstep(0.93, 1.0, rd.a));
  vec3 col = fog;
  if (clear > 0.001) {
    // 擦开的地方：清晰的窗外，留一层很薄的水膜
    vec3 wiped = (uSeeThrough ? vec3(0.0) : scene(q, 0.0012) * 1.05) + haze * 0.02 * dens;
    float rim = clear * (1.0 - clear) * 4.0;
    col = mix(fog, wiped, clear) + rim * haze * 0.16 * dens;
    if (uSeeThrough) alpha = mix(alpha, 0.02 * dens, clear) + rim * 0.16 * dens;
  }
  // 合成方式照搬 raindrop-fx 的 compose.glsl：阈值成形、按法线折射清晰的窗外、按光照明暗
  vec4 comp = vec4(rd.rgb + fd.rgb - 2.0 * rd.rgb * fd.rgb, max(rd.a, fd.a));
  float dmask = smoothstep(0.96, 0.99, comp.a);
  if (dmask > 0.001) {
    vec2 nn = comp.rg - 0.5;
    vec2 ruv = sv + vec2(-nn.x, nn.y) * (comp.b * 0.6 + 0.4) * uRefract;
    // 雾笺 demo 用的是轻度模糊的背景、漫反射 0.2–0.5、高光指数 48
    vec3 lens;
#if defined(NEBBIA_DEW) && NEBBIA_FOG_TEX
    // 凝露的细水珠又密又小，每个像素重算一遍窗外太贵：读预渲染好的同样模糊（0.006）的窗外，低分辨率下放大看不出差别。
    // 擦痕边的水珠（rd 占上风）照旧现算
    if (!uSeeThrough && fd.a > rd.a) lens = texture(uFogSharp, vec2(ruv.x - uSceneShift.x, 1.0 - ruv.y + uSceneShift.y)).rgb;
    else
#endif
    lens = scene(vec2(ruv.x * aspect, ruv.y), 0.006);
    vec3 normal = normalize(vec3(nn * 2.0, 1.0));
    vec3 lightDir = normalize(vec3(-1.0, 1.0, 2.0));
    float lambert = clamp(dot(lightDir, normal), 0.0, 1.0);
    float spec = pow(max(dot(normal, normalize(lightDir + vec3(0.0, 0.0, 1.0))), 0.0), 48.0);
    lens += (lambert - 0.8) * vec3(0.3, 0.3, 0.32) + spec * vec3(0.3, 0.3, 0.33) * uGlint;
    if (uSeeThrough) {
      // 和 raindrop-fx 一样，水珠是一颗透镜，里面是折射过来的背景。透明时拿不到窗后面的画面，折射的还是雾笺自己的
      // 窗外（选了水族箱折射水族箱，别的折射雨夜街景）。白雾时水珠不透明：街景比雾暗，和透出后面的字一样是暗的；
      // 深色雾时字透出的后面比雾亮，水珠也要以透出后面为主：街景留一半不到，边上一圈折射的暗边
      float dark = 1.0 - dot(uMistTint, vec3(0.333));
      float rimA = (1.0 - smoothstep(0.975, 0.995, comp.a)) * 0.21;
      float da = mix(1.0, 0.47 + rimA, dark);
      lens = max(lens, vec3(0.0)) * mix(1.0, 0.47, dark);
      alpha = mix(alpha, da, dmask);
    } else {
      // 车厢里的灯照在水珠表面：比纯透过去的窗外亮一点，不会在灰白的雾上显成黑点
      lens = max(lens, fog * 0.55) + haze * 0.08 * dens;
#ifdef NEBBIA_DEW
      // 凝露玻璃上的细小冷凝水珠（nebbiaCondensation.ts）：后面暗（水族箱的水、夜里的街）时照常画会像一粒粒脏点；
      // 按挂在玻璃上的水珠画，比雾亮一点、带一点高光。擦痕边的水珠（rd 占上风的地方）照旧
      float fine = step(rd.a, fd.a);
      lens = mix(lens, max(lens * 1.15, fog * (1.0 + 0.051 * uDewGlint)) + (spec * 0.544 + lambert * 0.068) * dens * uDewGlint, fine);
#endif
    }
    col = mix(col, lens, dmask);
  }
#ifdef NEBBIA_SOFT_HIGHLIGHT
  // 水族箱：聚光灯、水母、凝露的水珠叠在同一处会加过 1，烧成一块白。最亮的那个通道过了 0.75 以后
  // 按指数慢慢逼近 1，三个通道同比例压，颜色不变；0.75 以下不动
  if (!uSeeThrough) {
    float pk = max(col.r, max(col.g, col.b));
    if (pk > 0.75) col *= (0.75 + 0.25 * (1.0 - exp((0.75 - pk) / 0.25))) / pk;
  }
#endif
  outColor = uSeeThrough ? vec4(max(col, 0.0), alpha) : vec4(col, 1.0);
}`,k=O(!0),ee=O(!1),te=(e,t=`rain`)=>{let n=t===`dew`;return e===`aquarium`?{fog:C(S),main:O(!0,S,n),mainFallback:O(!1,S,n)}:n?{fog:w,main:O(!0,x,!0),mainFallback:O(!1,x,!0)}:{fog:w,main:k,mainFallback:ee}},A=`#version 300 es
in vec2 aCorner; in vec4 aRect; in vec2 aParam;
uniform vec2 uView;
out vec2 vUV; out float vSize; out float vAlpha;
void main() {
  vec2 p = aRect.xy + (aCorner - 0.5) * aRect.zw;
  vUV = aCorner; vSize = aParam.x; vAlpha = aParam.y;
  gl_Position = vec4(p.x / uView.x * 2.0 - 1.0, p.y / uView.y * 2.0 - 1.0, 0.0, 1.0);
}`,j=`#version 300 es
precision mediump float;
uniform sampler2D uSprite;
in vec2 vUV; in float vSize; in float vAlpha; out vec4 o;
void main() {
  vec4 c = texture(uSprite, vUV);
  c.a *= vAlpha;
  o = vec4(c.rg * c.a, vSize * c.a, c.a);
}`,M=26.88,N=.84,P=Math.PI*2,F=(e,t)=>e+Math.random()*(t-e),I=(e,t,n)=>{let r=Math.max(0,Math.min(1,(n-e)/(t-e)));return r*r*(3-2*r)},L=()=>{let e=document.createElement(`canvas`);return e.width=e.height=64,e},R=e=>{let t=[],n=[],r=[],i=F(.13,.18),a=e=>{let t=[];for(let n=F(.2,.4);n<e-.08;n+=F(.14,.28))Math.random()<.3||t.push({at:n,len:F(.1,.3)*(1-n),ang:F(.85,1.2),side:Math.random()<.25?Math.random()<.5?1:-1:0});return t};if(e===`bead`){n.push({x:F(-.05,.05),y:F(-.05,.05),r:F(.45,.55),a:.95});for(let e=0,t=4+Math.floor(Math.random()*3);e<t;e+=1){let e=Math.random()*P,t=F(.15,.32);n.push({x:Math.cos(e)*t,y:Math.sin(e)*t,r:F(.18,.3),a:F(.55,.8)})}for(let e=0,n=5+Math.floor(Math.random()*3);e<n;e+=1)t.push({ang:Math.random()*P,len:F(.45,.75),w:F(.04,.07),branches:[]})}else if(e===`clump`){let e=0,t=0,r=Math.random()*P;for(let i=0,a=7+Math.floor(Math.random()*10);i<a;i+=1){n.push({x:e,y:t,r:F(.1,.22),a:F(.6,.95)}),r+=F(-1.6,1.6);let i=F(.07,.15);e=Math.max(-.42,Math.min(.42,e+Math.cos(r)*i)),t=Math.max(-.42,Math.min(.42,t+Math.sin(r)*i))}}else{n.push({x:0,y:0,r:F(.1,.22),a:.9});let e=F(.7,1),r=new Set;Math.random()<.5&&r.add(Math.floor(Math.random()*6)),r.size&&Math.random()<.3&&r.add(Math.floor(Math.random()*6));for(let n=0;n<6;n+=1){if(r.has(n)&&Math.random()<.4)continue;let o=e*F(.86,1)*(r.has(n)?F(.3,.6):1);t.push({ang:n*P/6+F(-.05,.05),len:o,w:i*F(.9,1.1),branches:a(o)})}Math.random()<.5&&n.push({x:F(-.2,.2),y:F(-.2,.2),r:F(.12,.25),a:.6})}for(let e=0,i=6+Math.floor(Math.random()*14);e<i;e+=1)if(t.length&&Math.random()<.6){let e=t[Math.floor(Math.random()*t.length)],n=Math.random()*e.len;r.push({x:Math.cos(e.ang)*n+F(-.04,.04),y:Math.sin(e.ang)*n+F(-.04,.04),r:F(.025,.06)})}else{let e=n[Math.floor(Math.random()*n.length)],t=Math.random()*P,i=Math.random()*e.r;r.push({x:e.x+Math.cos(t)*i,y:e.y+Math.sin(t)*i,r:F(.03,.07)})}let o=n.slice().sort((e,t)=>t.r-e.r).slice(0,3).map(e=>({x:e.x,y:e.y,w:e.r*e.r})),s=o.reduce((e,t)=>e+t.w,0);for(let e of o)e.w/=s;return{arms:t,discs:n,specks:r,lumps:o}},z=(e,t)=>{let n=L(),r=n.getContext(`2d`),i=M;r.translate(32,32),r.lineCap=`round`;for(let t of e.discs){let e=r.createRadialGradient(t.x*i,t.y*i,0,t.x*i,t.y*i,t.r*i);e.addColorStop(0,`rgba(255,255,255,${t.a})`),e.addColorStop(.6,`rgba(255,255,255,${t.a*.6})`),e.addColorStop(1,`rgba(255,255,255,0)`),r.fillStyle=e,r.beginPath(),r.arc(t.x*i,t.y*i,t.r*i,0,P),r.fill()}for(let t of e.arms){r.save(),r.rotate(t.ang),r.strokeStyle=`rgba(255,255,255,0.55)`,r.lineWidth=t.w*i,r.beginPath(),r.moveTo(0,0),r.lineTo(t.len*i,0),r.stroke(),r.strokeStyle=`rgba(255,255,255,0.42)`,r.lineWidth=t.w*i*.75;for(let e of t.branches)for(let t of[-1,1])e.side&&e.side!==t||(r.beginPath(),r.moveTo(e.at*i,0),r.lineTo((e.at+Math.cos(e.ang)*e.len)*i,t*Math.sin(e.ang)*e.len*i),r.stroke());r.restore()}r.fillStyle=`rgba(255,255,255,0.95)`;for(let t of e.specks)r.beginPath(),r.arc(t.x*i,t.y*i,t.r*i,0,P),r.fill();let a=L().getContext(`2d`);a.filter=`blur(${t}px)`,a.drawImage(n,0,0);let o=a.getImageData(0,0,64,64).data,s=new Uint8ClampedArray(4096);for(let e=0;e<s.length;e+=1)s[e]=o[e*4+3];return s},B=(e,t)=>{let n=R(e),r=z(n,.35),i=z(n,2),a=[228,236,250],o=[196,212,236],s=[16,20,30],c=[];for(let e=0;e<16;e+=1){let t=e/15,n=t*.85,l=new ImageData(64,64),u=l.data;for(let e=0;e<r.length;e+=1){let c=r[e]/255,l=i[e]/255,d=I(n,n+.3,c),f=d*(.88-.45*t),p=t>0?I(n-.2,n,c)*I(.04,.12,c)*(1-d)*.32*Math.min(1,t*2):0,m=Math.min(1,l*2)*(1-d)*.2*(1-t),h=f+p;if(h+m<.004)continue;let g=I(.8,1,c)*(1-t),_=h+m*(1-h);for(let t=0;t<3;t+=1){let n=a[t]+(255-a[t])*g,r=h>0?(n*f+o[t]*p)/h:0;u[e*4+t]=(r*h+s[t]*m*(1-h))/_}u[e*4+3]=_*255}c.push(l)}return{type:e,row:t,stages:c,lumps:n.lumps}},V=e=>{let t=document.createElement(`canvas`),n=t.getContext(`2d`);return t.width=1024,t.height=64*e.length,e.forEach((e,t)=>e.stages.forEach((e,r)=>n.putImageData(e,r*64,t*64))),t},H=`
// glow：雾的光晕层；fu：屏幕 uv（y 向上）；n：冰面斜面（屏幕方向，y 向上）；seed：这块冰面的随机数（定它的折射角）
vec3 fixedFire(sampler2D glow, vec2 fu, vec2 n, float seed) {
  const vec3 W = vec3(0.3, 0.59, 0.11);
  vec2 e = vec2(0.025, 0.0);
  vec2 lg = vec2(dot(textureLod(glow, fu + e.xy, 0.0).rgb - textureLod(glow, fu - e.xy, 0.0).rgb, W),
                 dot(textureLod(glow, fu + e.yx, 0.0).rgb - textureLod(glow, fu - e.yx, 0.0).rgb, W));
  float ll = length(lg), nl = length(n);
  if (ll < 1e-4 || nl < 1e-4) return vec3(0.0);
  float x = (dot(n / nl, lg / ll) - mix(0.6, 0.95, seed)) / 0.05;
  if (abs(x) >= 1.0) return vec3(0.0);
  float h = (x * 0.5 + 0.5) * 0.75;
  vec3 spec = clamp(abs(fract(h + vec3(0.0, 0.667, 0.333)) * 6.0 - 3.0) - 1.0, 0.0, 1.0);
  return spec * (1.0 - x * x) * smoothstep(0.002, 0.03, ll);
}
`,U=`9.0`,W=`#version 300 es
in vec2 aCorner; in vec4 aA; in vec4 aB; in vec2 aC;
uniform vec2 uView, uShift;
out vec2 vLocal; out vec2 vHalo; out vec2 vFu; flat out vec4 vB; flat out float vR; flat out float vSeed;
void main() {
  vec2 c = (aCorner * 2.0 - 1.0) * ${2.6.toFixed(2)};
  float cs = cos(aA.w), sn = sin(aA.w);
  vec2 off = c * aA.z;
  vec2 p = aA.xy + vec2(off.x * cs - off.y * sn, off.x * sn + off.y * cs) + uShift;
  // vLocal：贴图里的坐标（单位是雪花半径，压扁之前）；vHalo：糊光、柔光用的（不压扁）
  vLocal = c / aC; vHalo = c; vB = aB; vR = aA.z;
  // 每片雪自己的随机数（闪光用）：贴上以后转角、种类都不再变
  vSeed = fract(sin(aA.w * 12.9898 + aB.y * 78.233) * 43758.5453);
  // 雾的预渲染纹理第 0 行是屏幕底部
  vFu = vec2(p.x / uView.x, 1.0 - p.y / uView.y);
  gl_Position = vec4(p.x / uView.x * 2.0 - 1.0, 1.0 - p.y / uView.y * 2.0, 0.0, 1.0);
}`,G=`#version 300 es
precision mediump float;
uniform sampler2D uAtlas, uGlow, uBehind;
uniform bool uLit; uniform vec2 uGrid; uniform highp vec2 uView;
in vec2 vLocal; in vec2 vHalo; in vec2 vFu; flat in vec4 vB; flat in float vR; flat in float vSeed;
out vec4 o;
${H}
vec4 tile(float col, vec2 t, float bias) { return texture(uAtlas, (vec2(col, vB.y) + t) / uGrid, bias); }
void main() {
  float p = vB.w;
  vec2 t = 0.5 + vLocal * ${(N/2).toFixed(3)};
  // 相邻两个融化阶段交叉；还没飘到焦点上的取更糊的一级。不放在分支里取（分支里取样的 mipmap 级别不可靠），格子外面乘 0
  float st = floor(vB.x), fr = vB.x - st, bias = 2.0 * (1.0 - p);
  vec2 tc = clamp(t, 0.0, 1.0);
  vec4 c = mix(tile(st, tc, bias), tile(min(st + 1.0, uGrid.x - 1.0), tc, bias), fr);
  c *= vB.z * float(t == tc);
  // 身后的光：颜色带大半（还看得出是白雪），亮度跟着走：暗处压得很暗，灯下亮过雾
  vec3 L = uLit ? texture(uGlow, vFu).rgb : vec3(0.42);
  float lum = dot(L, vec3(0.3, 0.59, 0.11));
  vec3 hue = clamp(mix(vec3(1.0), L / max(lum, 0.02), 0.8), 0.0, 2.2);
  vec3 lit = hue * clamp(0.22 + 2.5 * lum, 0.0, 1.4);
  vec3 rgb = c.rgb * lit;
  // 逆光（身后有亮光）：雪像冰一样被照透。冰晶的厚薄（贴图的不透明度）当成表面的起伏，斜的地方把身后的窗外往里折（取主画面那层稍糊的窗外，uBehind），
  // 透过来的光比四周亮一点；薄的边、斜面上再亮一道。暗处照旧是白雪。只在对上焦（贴上玻璃）后，化得越多越弱（化成水交给水珠去折射）
  float back = float(uLit) * smoothstep(0.08, 0.45, lum) * p * (1.0 - 0.7 * vB.x / ${15 .toFixed(1)});
  float a0 = c.a / max(vB.z, 0.001);
  vec2 n = vec2(dFdx(a0), dFdy(a0)) * vR;
  float edge = clamp(length(n) * 0.5, 0.0, 1.0);
  // 色散（比真冰放大了不少，要看得出）：红、绿、蓝折得不一样多，棱上再沿斜的方向分开一点，透过来的窗外在边上错出彩边、颜色加浓；
  vec2 bend = n * vR * 0.7 / uView, split = n / max(length(n), 0.001) * vR * 0.35 * edge / uView;
  vec3 through = vec3(texture(uBehind, vFu - bend * 0.8 - split).r, texture(uBehind, vFu - bend).g, texture(uBehind, vFu - bend * 1.25 + split).b);
  vec3 gray = vec3(dot(through, vec3(0.3333)));
  through = gray + (through - gray) * (1.0 + 1.5 * edge);
  vec3 ice = (through * (1.1 + 0.3 * edge) + min(L, vec3(1.2)) * edge * 0.3) * c.a + c.rgb * lit * 0.15;
  rgb = mix(rgb, ice, back * 0.85);
  float d = length(vHalo);
  // 还没飘到焦点上：外面套一圈糊的
  float ha = 0.35 * (1.0 - p) * max(0.0, 1.0 - d / (1.6 - 0.6 * p));
  rgb = vec3(0.92, 0.95, 1.0) * lit * ha + rgb * (1.0 - ha);
  float a = ha + c.a * (1.0 - ha);
  // 靠近亮光：外面散一圈很淡的柔光（只加颜色，比雪花大不了多少），化得越多越淡；没有光照（透明）时不加
  float soft = float(uLit) * smoothstep(0.12, 0.6, lum) * 0.45 * exp(-d * d * 2.4) * vB.z * (1.0 - vB.x / ${15 .toFixed(1)});
  // 火彩（色散）：贴图按格子分，每格一块冰面、有自己固定的折射角，只有冰棱上（斜面）的格子会出；颜色由斜面和光的方向定（nebbiaFireGlsl.ts），
  // 只加光，亮度按身后光的亮度给，不跟灯的颜色走；约四成格子有朝得对的冰面
  vec2 g = tc * ${U}, gc = floor(g), gf = fract(g) - 0.5;
  float h = fract(sin(dot(gc, vec2(12.9898, 78.233)) + vSeed * 91.7) * 43758.5453);
  vec3 fire = fixedFire(uGlow, vFu, n, fract(h * 7.31)) * step(h, 0.4);
  float glint = back * exp(-dot(gf, gf) * 18.0) * edge * (0.6 + 2.0 * lum) * vB.z;
  o = vec4(rgb + min(L, vec3(1.2)) * soft + fire * glint * 2.2, a);
}`,K=class{constructor(e,t){this.gl=e,this.prog=null,this.uView=null,this.uShift=null,this.uGrid=null,this.rows=0,this.capacity=0,this.atlas=s(e,t),this.vao=e.createVertexArray(),e.bindVertexArray(this.vao),e.bindBuffer(e.ARRAY_BUFFER,e.createBuffer()),e.bufferData(e.ARRAY_BUFFER,new Float32Array([0,0,1,0,0,1,1,1]),e.STATIC_DRAW),e.enableVertexAttribArray(0),e.vertexAttribPointer(0,2,e.FLOAT,!1,0,0),this.buf=e.createBuffer(),e.bindBuffer(e.ARRAY_BUFFER,this.buf),e.enableVertexAttribArray(1),e.vertexAttribPointer(1,4,e.FLOAT,!1,40,0),e.vertexAttribDivisor(1,1),e.enableVertexAttribArray(2),e.vertexAttribPointer(2,4,e.FLOAT,!1,40,16),e.vertexAttribDivisor(2,1),e.enableVertexAttribArray(3),e.vertexAttribPointer(3,2,e.FLOAT,!1,40,32),e.vertexAttribDivisor(3,1),e.bindVertexArray(null)}attach(e,t,n,r){let{gl:i}=this;this.prog=e,i.useProgram(e),this.uView=i.getUniformLocation(e,`uView`),this.uShift=i.getUniformLocation(e,`uShift`),this.uGrid=i.getUniformLocation(e,`uGrid`),i.uniform1i(i.getUniformLocation(e,`uAtlas`),this.atlas.unit),i.uniform1i(i.getUniformLocation(e,`uGlow`),t),i.uniform1i(i.getUniformLocation(e,`uBehind`),n),i.uniform1i(i.getUniformLocation(e,`uLit`),+!!r)}setAtlas(e,t){let{gl:n}=this;this.rows=t,n.activeTexture(n.TEXTURE0+this.atlas.unit),n.bindTexture(n.TEXTURE_2D,this.atlas.t),n.pixelStorei(n.UNPACK_PREMULTIPLY_ALPHA_WEBGL,!0),n.texImage2D(n.TEXTURE_2D,0,n.RGBA,n.RGBA,n.UNSIGNED_BYTE,e),n.pixelStorei(n.UNPACK_PREMULTIPLY_ALPHA_WEBGL,!1),n.generateMipmap(n.TEXTURE_2D),n.texParameteri(n.TEXTURE_2D,n.TEXTURE_MIN_FILTER,n.LINEAR_MIPMAP_LINEAR),n.texParameteri(n.TEXTURE_2D,n.TEXTURE_MAX_LEVEL,4)}draw(e,t,n,r,i){let{gl:a}=this;this.prog&&this.rows&&t&&(a.useProgram(this.prog),a.bindVertexArray(this.vao),a.bindBuffer(a.ARRAY_BUFFER,this.buf),e.length>this.capacity&&(a.bufferData(a.ARRAY_BUFFER,e.byteLength,a.DYNAMIC_DRAW),this.capacity=e.length),a.bufferSubData(a.ARRAY_BUFFER,0,e,0,t*10),a.uniform2f(this.uView,n,r),a.uniform2f(this.uShift,i[0],i[1]),a.uniform2f(this.uGrid,16,this.rows),a.enable(a.BLEND),a.blendFunc(a.ONE,a.ONE_MINUS_SRC_ALPHA),a.drawArraysInstanced(a.TRIANGLE_STRIP,0,4,t),a.disable(a.BLEND),a.bindVertexArray(null))}},q={count:100,size:1,halo:2,speed:1,glow:1},J=Math.PI*2,Y=.4,X=4.5*6.95,Z=(e,t)=>e+Math.random()*(t-e),Q=e=>e*e*(3-2*e),ne=(e,t,n)=>e+Math.atan2(Math.sin(t-e),Math.cos(t-e))*n,re=class{constructor(){this.flies=[],this.out=new Float32Array,this.W=0,this.H=0,this.k=1,this.spawnAcc=0,this.palette=[[1,.81,.54],[.54,.8,1],[.8,.54,1],[1,.68,.32],[1,.95,.85]]}setPalette(e,t,n){let r=[e,t,n].map(e=>{let t=Math.max(e[0],e[1],e[2],.05);return e.map(e=>e/t)});this.palette=[...r,...this.palette.slice(3)]}resize(e,t){if(e===this.W&&t===this.H)return;let n=!this.W,r=e/(this.W||e),i=t/(this.H||t);if(this.W=e,this.H=t,this.k=t/1e3,n){let n=Math.round(q.count*e*t/921600);this.flies=Array.from({length:n},()=>{let e=this.spawn();return e.age=Math.random()*(e.app+e.hold+e.leave),e.px=e.py=NaN,e})}else for(let e of this.flies)e.x*=r,e.y*=i,e.px*=r,e.py*=i}spawn(){let{k:e}=this,t=q.speed||1,n=Math.random()*J,r=Z(80,220)*e,i=Math.random()*this.W,a=Math.random()*this.H,o=Math.cos(n)*r,s=Math.sin(n)*r-Z(0,50)*e,c=Z(1.5,3)/t,l=Z(3,6),u=5+Math.floor(Math.random()*6);return{x:i,y:a,r:(Math.random()<.3?Z(4.5,6):Z(2.5,4.5))*e*q.size,app:c,ox:o,oy:s,zig:Z(12,30)*e,zf:Z(1.2,2.2),fp:Math.random()*J,hold:l*(u-1+Y+.05),leave:Z(.8,1.4)/t,dir:Math.random()*J,far:Z(120,260)*e,age:0,crawl:Math.random()*J,blink:l,bp:(1-c/l%1)%1,tint:Math.floor(Math.random()*5),px:i+o,py:a+s,head:Math.atan2(-s,-o)}}step(e){let t=q,{W:n,H:r}=this;if(!n||!r)return[this.out,0];let i=t.count*n*r/921600,a=3.35/(t.speed||1)+X;for(this.spawnAcc+=e*i/a;this.spawnAcc>=1;--this.spawnAcc)this.flies.push(this.spawn());let o=this.flies,s=0;for(let t of o)(t.age+=e)<t.app+t.hold+t.leave&&(o[s++]=t);o.length=s,this.out.length<s*12&&(this.out=new Float32Array(Math.ceil(s*1.5)*12));for(let t=0;t<s;t+=1)this.pack(o[t],t*12,e);return[this.out,s]}pack(e,t,r){let{k:i}=this,a=e.x,o=e.y,s=1,c=.92,l=1,u=0,d=Math.min(1,e.age/e.app),f=(e.age-e.app-e.hold)/e.leave;if(d<1){let t=J*2.5,r=n(d+.85*(Math.sin(t*d+e.fp)-Math.sin(e.fp)*(1-d)-Math.sin(t+e.fp)*d)/t),i=(1-r)**2,f=Math.hypot(e.ox,e.oy)||1,p=-e.oy/f,m=e.ox/f,h=e.zig*(1-r)*(Math.sin(e.age*e.zf*J+e.fp)+.5*Math.sin(e.age*e.zf*2.7*J+e.fp*2));a+=e.ox*i+p*h,o+=e.oy*i+m*h,s=.7+.3*d,c*=.45+.55*d,l=d,u=(.6+.4*Math.abs(Math.sin(e.age*47+e.fp)))*(1-d**4)}else if(f<0){let t=e.age-e.app,s=Q(n((Math.sin(t*.9+e.fp)-.2)/.4));e.crawl+=(Math.random()-.5)*.78*Math.sqrt(r),e.x+=Math.cos(e.crawl)*7*i*s*r,e.y+=Math.sin(e.crawl)*7*i*s*r,a=e.x,o=e.y,u=.35*Math.max(0,Math.sin(t*1.7+e.fp*3)-.94)/.06*Math.abs(Math.sin(t*40))}else{let t=f*f*e.far,n=e.zig*.6*f*Math.sin(e.age*e.zf*J+e.fp);a+=Math.cos(e.dir)*t-Math.sin(e.dir)*n,o+=Math.sin(e.dir)*t+Math.cos(e.dir)*n,s=1-.3*f,l=1-f,c*=1-f**1.5,u=.6+.4*Math.abs(Math.sin(e.age*47+e.fp))}let p=a-e.px,m=o-e.py;p*p+m*m>.0025*i*i&&(e.head=ne(e.head,Math.atan2(m,p),Math.min(1,r*10))),e.px=a,e.py=o;let h=(e.age/e.blink+e.bp)%1,g=h<.15?Q(h/.15):h<Y?1-Q((h-.15)/.25):0,_=this.out;_[t]=a,_[t+1]=o,_[t+2]=e.r*s,_[t+3]=c,_[t+4]=l,_[t+5]=f<0?1:1-f,_[t+6]=e.head,_[t+7]=u,_[t+8]=g*q.glow*(f<0?1:1-f);let v=this.palette[e.tint];_[t+9]=v[0],_[t+10]=v[1],_[t+11]=v[2]}},$=`#version 300 es
in vec2 aCorner; in vec4 aA; in vec4 aB; in vec4 aC;
uniform vec2 uView; uniform float uHalo;
out vec2 vLocal; out vec2 vFu; flat out vec4 vP; flat out vec4 vGlow;
void main() {
  // 方块要盖住糊开的虫子和柔光：柔光到方块边上要淡到看不出（低于 1/255），不然灯下看得见方框
  float ext = max(2.6, 4.1 * uHalo);
  vec2 c = (aCorner * 2.0 - 1.0) * ext;
  // 按身子朝向转：vLocal 是虫子自己的坐标（x 朝头，单位是半径）
  float cs = cos(aB.z), sn = sin(aB.z);
  vec2 off = c * aA.z;
  vec2 p = aA.xy + vec2(off.x * cs - off.y * sn, off.x * sn + off.y * cs);
  vLocal = c; vP = vec4(aA.w, aB.x, aB.y, aB.w); vGlow = aC;
  // 雾的预渲染纹理第 0 行是屏幕底部
  vFu = vec2(p.x / uView.x, 1.0 - p.y / uView.y);
  gl_Position = vec4(p.x / uView.x * 2.0 - 1.0, 1.0 - p.y / uView.y * 2.0, 0.0, 1.0);
}`,ie=`#version 300 es
precision mediump float;
uniform sampler2D uGlow;
// uHalo 两边都用：和顶点着色器的精度要一样
uniform bool uLit; uniform highp float uHalo;
in vec2 vLocal; in vec2 vFu; flat in vec4 vP; flat in vec4 vGlow;
out vec4 o;
// 一个椭圆（中心 c、半轴 ab、转 a 弧度）的覆盖，边缘宽 e
float blob(vec2 q, vec2 c, vec2 ab, float a, float e) {
  q -= c;
  float cs = cos(a), sn = sin(a);
  q = vec2(q.x * cs + q.y * sn, -q.x * sn + q.y * cs) / ab;
  return 1.0 - smoothstep(1.0 - e, 1.0 + e, length(q));
}
void main() {
  float p = vP.y, flap = vP.w;
  vec2 q = vLocal;
  // 没对上焦：边缘糊开
  float e = 0.18 + 0.9 * (1.0 - p);
  // 身子：头、胸、长一点的肚子
  float body = max(max(blob(q, vec2(0.62, 0.0), vec2(0.17, 0.15), 0.0, e),
                       blob(q, vec2(0.3, 0.0), vec2(0.22, 0.2), 0.0, e)),
                   blob(q, vec2(-0.25, 0.0), vec2(0.48, 0.19), 0.0, e));
  // 翅膀：收拢时盖在背上往后，张开时往两边摆；扇得快，看着是一片半透明的糊影
  float wa = 0.12 + 1.2 * flap, we = e + 0.3 * flap;
  float wing = max(blob(q, vec2(-0.15 + 0.2 * flap, 0.08 + 0.45 * flap), vec2(0.62, 0.17), wa, we),
                   blob(q, vec2(-0.15 + 0.2 * flap, -0.08 - 0.45 * flap), vec2(0.62, 0.17), -wa, we));
  // 身后的光：颜色带大半，亮度跟着走：暗处压得很暗，灯下亮过雾
  vec3 L = uLit ? texture(uGlow, vFu).rgb : vec3(0.42);
  float lum = dot(L, vec3(0.3, 0.59, 0.11));
  vec3 hue = clamp(mix(vec3(1.0), L / max(lum, 0.02), 0.8), 0.0, 2.2);
  vec3 lit = hue * clamp(0.22 + 2.5 * lum, 0.0, 1.4);
  // 身子淡淡的白；翅膀更薄、透光，叠在身子上面
  float ab = body * 0.5, aw = wing * (0.3 - 0.1 * flap);
  float ca = (aw + ab * (1.0 - aw)) * vP.x;
  vec3 rgb = (vec3(0.95, 0.97, 1.0) * aw + vec3(0.86, 0.88, 0.92) * ab * (1.0 - aw)) * lit * vP.x;
  float d = length(q);
  // 还没飘到焦点上：外面套一圈糊的
  float ha = 0.35 * (1.0 - p) * max(0.0, 1.0 - d / (1.6 - 0.6 * p)) * vP.x;
  rgb = vec3(0.92, 0.95, 1.0) * lit * ha + rgb * (1.0 - ha);
  float a = ha + ca * (1.0 - ha);
  // 靠近亮光：散一圈很淡的柔光（只加颜色）；没有光照（透明）时不加
  // 身后已经很亮（聚光灯正照着）时柔光收掉大半：再往亮处加光只会叠成一片白
  float soft = float(uLit) * smoothstep(0.12, 0.6, lum) * (1.0 - 0.75 * smoothstep(0.6, 1.2, lum)) * 0.45 * exp(-d * d * 0.3 / (uHalo * uHalo)) * vP.z;
  // 尾巴的光：肚子尖上一小团，外面一小圈光晕，只加颜色（自己发光，不挡后面）；没对上焦时糊大
  float dt = length(q - vec2(-0.55, 0.0)), gr = 0.45 + 0.35 * (1.0 - p);
  float lamp = (1.0 - smoothstep(0.0, gr, dt)) + 0.8 * exp(-dt * dt * 0.7);
  // 中心偏白一点（亮到发白），外圈是灯光色
  vec3 tint = mix(vGlow.yzw, vec3(1.0), 0.35 * (1.0 - smoothstep(0.0, gr, dt)));
  vec3 glow = tint * lamp * vGlow.x * vP.x * mix(0.5, 1.0, p);
  o = vec4(rgb + min(L, vec3(1.2)) * soft + glow, a);
}`,ae=class{constructor(e){this.gl=e,this.prog=null,this.uView=null,this.uHalo=null,this.vao=e.createVertexArray(),e.bindVertexArray(this.vao),e.bindBuffer(e.ARRAY_BUFFER,e.createBuffer()),e.bufferData(e.ARRAY_BUFFER,new Float32Array([0,0,1,0,0,1,1,1]),e.STATIC_DRAW),e.enableVertexAttribArray(0),e.vertexAttribPointer(0,2,e.FLOAT,!1,0,0),this.buf=e.createBuffer(),e.bindBuffer(e.ARRAY_BUFFER,this.buf),e.enableVertexAttribArray(1),e.vertexAttribPointer(1,4,e.FLOAT,!1,48,0),e.vertexAttribDivisor(1,1),e.enableVertexAttribArray(2),e.vertexAttribPointer(2,4,e.FLOAT,!1,48,16),e.vertexAttribDivisor(2,1),e.enableVertexAttribArray(3),e.vertexAttribPointer(3,4,e.FLOAT,!1,48,32),e.vertexAttribDivisor(3,1),e.bindVertexArray(null)}attach(e,t,n){let{gl:r}=this;this.prog=e,r.useProgram(e),this.uView=r.getUniformLocation(e,`uView`),this.uHalo=r.getUniformLocation(e,`uHalo`),r.uniform1i(r.getUniformLocation(e,`uGlow`),t),r.uniform1i(r.getUniformLocation(e,`uLit`),+!!n)}draw(e,t,n,r){let{gl:i}=this;this.prog&&t&&(i.useProgram(this.prog),i.bindVertexArray(this.vao),i.bindBuffer(i.ARRAY_BUFFER,this.buf),i.bufferData(i.ARRAY_BUFFER,e.subarray(0,t*12),i.DYNAMIC_DRAW),i.uniform2f(this.uView,n,r),i.uniform1f(this.uHalo,Math.max(.05,q.halo)),i.enable(i.BLEND),i.blendFunc(i.ONE,i.ONE_MINUS_SRC_ALPHA),i.drawArraysInstanced(i.TRIANGLE_STRIP,0,4,t),i.disable(i.BLEND),i.bindVertexArray(null))}},oe=e=>{if(!e.getExtension(`EXT_color_buffer_float`))return!1;let t=e.createTexture(),n=e.createFramebuffer();e.bindTexture(e.TEXTURE_2D,t),e.texImage2D(e.TEXTURE_2D,0,e.RGBA16F,1,1,0,e.RGBA,e.HALF_FLOAT,null),e.bindFramebuffer(e.FRAMEBUFFER,n),e.framebufferTexture2D(e.FRAMEBUFFER,e.COLOR_ATTACHMENT0,e.TEXTURE_2D,t,0);let r=e.checkFramebufferStatus(e.FRAMEBUFFER)===e.FRAMEBUFFER_COMPLETE;return e.bindFramebuffer(e.FRAMEBUFFER,null),e.bindTexture(e.TEXTURE_2D,null),e.deleteFramebuffer(n),e.deleteTexture(t),r||console.warn(`[Nebbia] fog prepass framebuffer incomplete, computing the fog in the main pass`),r},se=class{constructor(e,t,n=`night`,r=`rain`){this.gl=e,this.done=!1,this.parallel=e.getExtension(`KHR_parallel_shader_compile`);let o=i(e,e.VERTEX_SHADER,y),s=t=>i(e,e.FRAGMENT_SHADER,t),c=te(n,r);this.programs={fog:t?a(e,o,s(c.fog),{aPos:0}):null,main:a(e,o,s(t?c.main:c.mainFallback),{aPos:0}),sprite:a(e,i(e,e.VERTEX_SHADER,A),s(j),{aCorner:0,aRect:1,aParam:2}),mask:a(e,i(e,e.VERTEX_SHADER,u),s(d),{aCorner:0,aSeg:1,aParam:2}),bldFill:n===`night`?a(e,o,s(T),{aPos:0}):null,noise:a(e,o,s(D),{aPos:0}),flake:r===`snow`?a(e,i(e,e.VERTEX_SHADER,W),s(G),{aCorner:0,aA:1,aB:2,aC:3}):null,firefly:r===`dew`?a(e,i(e,e.VERTEX_SHADER,$),s(ie),{aCorner:0,aA:1,aB:2,aC:3}):null}}poll(){if(this.done)return!0;let{gl:e,parallel:t}=this,n=[this.programs.fog,this.programs.main,this.programs.sprite,this.programs.mask,this.programs.bldFill,this.programs.noise,this.programs.flake,this.programs.firefly].filter(e=>!!e);if(t&&!n.every(n=>e.getProgramParameter(n,t.COMPLETION_STATUS_KHR)))return!1;for(let t of n)o(e,t);for(let t of n)for(let n of e.getAttachedShaders(t)??[])e.deleteShader(n);return this.done=!0,!0}};export{r as _,K as a,V as c,l as d,c as f,n as g,e as h,re as i,B as l,t as m,oe as n,N as o,s as p,ae as r,M as s,se as t,f as u};