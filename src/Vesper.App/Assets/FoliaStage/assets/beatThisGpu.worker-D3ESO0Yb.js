const e=`
fn erf_f32(v: f32) -> f32 {
  let a = abs(v);
  let x = 1.0 / (1.0 + 0.3275911 * a);
  let y = 1.0 - ((((1.061405429 * x - 1.453152027) * x + 1.421413741) * x - 0.284496736) * x + 0.254829592) * x * exp(-a * a);
  return select(-y, y, v >= 0.0);
}
fn gelu(v: f32) -> f32 { return 0.5 * v * (1.0 + erf_f32(v * 0.7071067811865476)); }
`,t=`
struct P { frames: u32, bins: u32 }
@group(0) @binding(0) var<storage, read> mel: array<f32>;      // [T, bins]
@group(0) @binding(1) var<storage, read> scale: array<f32>;
@group(0) @binding(2) var<storage, read> shift: array<f32>;
@group(0) @binding(3) var<storage, read_write> out: array<f32>; // [bins, T]
@group(0) @binding(4) var<uniform> p: P;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3u) {
  let i = id.x;
  if (i >= p.frames * p.bins) { return; }
  let f = i / p.frames;
  let t = i % p.frames;
  out[i] = mel[t * p.bins + f] * scale[f] + shift[f];
}
`,n=`
${e}
struct P { fout: u32, frames: u32, cin: u32, cout: u32, kf: u32 }
@group(0) @binding(0) var<storage, read> x: array<f32>;
@group(0) @binding(1) var<storage, read> w: array<f32>;   // [kf, 3, cin, cout] = [K, cout]
@group(0) @binding(2) var<storage, read> b: array<f32>;
@group(0) @binding(3) var<storage, read_write> y: array<f32>;
@group(0) @binding(4) var<uniform> p: P;
var<workgroup> as_: array<f32, 1024>; // [64 rows][16 k]
var<workgroup> bs_: array<f32, 1024>; // [16 k][64 cols]

fn gather(row: u32, k: u32) -> f32 {
  let ci = k % p.cin;
  let tap = k / p.cin;
  let dt = tap % 3u;
  let df = tap / 3u;
  let t = row % p.frames;
  let fo = row / p.frames;
  let ti = i32(t) + i32(dt) - 1;
  if (ti < 0 || ti >= i32(p.frames)) { return 0.0; }
  return x[((fo * p.kf + df) * p.frames + u32(ti)) * p.cin + ci];
}

@compute @workgroup_size(16, 16)
fn main(@builtin(workgroup_id) wg: vec3u, @builtin(local_invocation_id) l: vec3u, @builtin(local_invocation_index) li: u32) {
  let m = p.fout * p.frames;
  let kTotal = p.kf * 3u * p.cin;
  let row0 = wg.y * 64u;
  let col0 = wg.x * 64u;
  var acc: array<array<f32, 4>, 4>;
  for (var k0 = 0u; k0 < kTotal; k0 += 16u) {
    for (var i = 0u; i < 4u; i++) {
      let idx = li + i * 256u;
      let ar = row0 + idx / 16u;
      let ak = k0 + idx % 16u;
      var av = 0.0;
      if (ar < m && ak < kTotal) { av = gather(ar, ak); }
      as_[idx] = av;
      let bk = k0 + idx / 64u;
      let bc = col0 + idx % 64u;
      var bv = 0.0;
      if (bk < kTotal && bc < p.cout) { bv = w[bk * p.cout + bc]; }
      bs_[idx] = bv;
    }
    workgroupBarrier();
    for (var kk = 0u; kk < 16u; kk++) {
      var a: array<f32, 4>;
      var bb: array<f32, 4>;
      for (var i = 0u; i < 4u; i++) { a[i] = as_[(l.y * 4u + i) * 16u + kk]; }
      for (var j = 0u; j < 4u; j++) { bb[j] = bs_[kk * 64u + l.x * 4u + j]; }
      for (var i = 0u; i < 4u; i++) {
        for (var j = 0u; j < 4u; j++) { acc[i][j] += a[i] * bb[j]; }
      }
    }
    workgroupBarrier();
  }
  for (var i = 0u; i < 4u; i++) {
    let r = row0 + l.y * 4u + i;
    if (r >= m) { continue; }
    for (var j = 0u; j < 4u; j++) {
      let c = col0 + l.x * 4u + j;
      if (c >= p.cout) { continue; }
      y[r * p.cout + c] = gelu(acc[i][j] + b[c]);
    }
  }
}
`,r=(t,n=`f32`)=>{let r=n!==`f32`;return`
${r?`enable f16;`:``}
${e}
struct P { m: u32, n: u32, k: u32, flags: u32, posDiv: u32, posMod: u32, dmodel: u32, wStride: u32 }
@group(0) @binding(0) var<storage, read> x: array<f32>;
${t}
@group(0) @binding(2) var<storage, read> bias: array<f32>;
@group(0) @binding(3) var<storage, read_write> y: array<f32>;
@group(0) @binding(4) var<storage, read> table: array<f32>; // [pos][16][cos, sin]
@group(0) @binding(5) var<uniform> p: P;
${r?`var<workgroup> as_: array<vec4h, 256>; // [16 k][16 row quads]
var<workgroup> bs_: array<vec4h, 256>; // [16 k][16 col quads]
var<workgroup> sums_: array<vec4f, 256>; // per-loader row sums of squares
const A_SCALE = 64.0;`:`var<workgroup> as_: array<vec4f, 256>; // [16 k][16 row quads]; afterwards the per-loader row sums of squares
var<workgroup> bs_: array<vec4f, 256>; // [16 k][16 col quads]`}
@compute @workgroup_size(16, 16)
fn main(@builtin(workgroup_id) wg: vec3u, @builtin(local_invocation_id) l: vec3u, @builtin(local_invocation_index) li: u32) {
  let row0 = wg.y * 64u;
  let col0 = wg.x * 64u;
  var acc: array<vec4f, 4>; // acc[j] = rows 4*l.y..+3 of column 4*l.x + j
  // Loader roles: A - invocation li reads k = li % 16 of row quad li / 16; B - k = li / 16, col quad li % 16.
  let aK = li % 16u;
  let aQuad = li / 16u;
  let bK = li / 16u;
  let bQuad = li % 16u;
  var squares = vec4f(0.0);
  for (var k0 = 0u; k0 < p.k; k0 += 16u) {
    var av = vec4f(0.0);
    let ak = k0 + aK;
    if (ak < p.k) {
      for (var r = 0u; r < 4u; r++) {
        let row = row0 + aQuad * 4u + r;
        if (row < p.m) { av[r] = x[row * p.k + ak]; }
      }
    }
    squares = fma(av, av, squares);
    as_[aK * 16u + aQuad] = ${r?`vec4h(select(av, av / A_SCALE, (p.flags & 4u) != 0u))`:`av`};
    var bv = ${r?`vec4h`:`vec4f`}(0.0);
    let bk = k0 + bK;
    let bc = col0 + bQuad * 4u;
    if (bk < p.k && bc < p.n) { bv = weightQuad((bk * p.wStride + bc) / 4u); }
    bs_[bK * 16u + bQuad] = bv;
    workgroupBarrier();
${r?`${n===`math`?`    var part: array<vec4h, 4>;
`:``}    for (var kk = 0u; kk < 16u; kk++) {
      let a = as_[kk * 16u + l.y];
      let b = bs_[kk * 16u + l.x];
${n===`math`?`      part[0] = fma(a, vec4h(b.x), part[0]);
      part[1] = fma(a, vec4h(b.y), part[1]);
      part[2] = fma(a, vec4h(b.z), part[2]);
      part[3] = fma(a, vec4h(b.w), part[3]);
    }
    for (var j = 0u; j < 4u; j++) { acc[j] += vec4f(part[j]); }`:`      let af = vec4f(a);
      let bf = vec4f(b);
      acc[0] = fma(af, vec4f(bf.x), acc[0]);
      acc[1] = fma(af, vec4f(bf.y), acc[1]);
      acc[2] = fma(af, vec4f(bf.z), acc[2]);
      acc[3] = fma(af, vec4f(bf.w), acc[3]);
    }`}`:`    for (var kk = 0u; kk < 16u; kk++) {
      let a = as_[kk * 16u + l.y];
      let b = bs_[kk * 16u + l.x];
      acc[0] = fma(a, vec4f(b.x), acc[0]);
      acc[1] = fma(a, vec4f(b.y), acc[1]);
      acc[2] = fma(a, vec4f(b.z), acc[2]);
      acc[3] = fma(a, vec4f(b.w), acc[3]);
    }`}
    workgroupBarrier();
  }
  // The 16 loaders of row quad q are invocations 16q..16q+15; their partial sums meet in ${r?`sums_`:`as_`}.
  ${r?`sums_`:`as_`}[li] = squares;
  workgroupBarrier();
  var rowScale = vec4f(1.0);
  if ((p.flags & 4u) != 0u) {
    var total = vec4f(0.0);
    for (var t = 0u; t < 16u; t++) { total += ${r?`sums_`:`as_`}[l.y * 16u + t]; }
    rowScale = ${r?`A_SCALE`:`1.0`} / max(sqrt(total), vec4f(1e-12));
  }
  for (var i = 0u; i < 4u; i++) {
    let r = row0 + l.y * 4u + i;
    if (r >= p.m) { continue; }
    var v: array<f32, 4>;
    for (var j = 0u; j < 4u; j++) {
      let c = min(col0 + l.x * 4u + j, p.n - 1u);
      v[j] = acc[j][i] * rowScale[i] + bias[c];
    }
    let c0 = col0 + l.x * 4u;
    if ((p.flags & 8u) != 0u && c0 < 2u * p.dmodel) {
      let pos = (r / p.posDiv) % p.posMod;
      for (var pair = 0u; pair < 2u; pair++) {
        let t = (pos * 16u + ((c0 + 2u * pair) % 32u) / 2u) * 2u;
        let cs = table[t];
        let sn = table[t + 1u];
        let x0 = v[2u * pair];
        let x1 = v[2u * pair + 1u];
        v[2u * pair] = x0 * cs - x1 * sn;
        v[2u * pair + 1u] = x1 * cs + x0 * sn;
      }
    }
    for (var j = 0u; j < 4u; j++) {
      let c = c0 + j;
      if (c >= p.n) { continue; }
      var res = v[j];
      let o = r * p.n + c;
      if ((p.flags & 1u) != 0u) { res = gelu(res); }
      if ((p.flags & 2u) != 0u) { res += y[o]; }
      y[o] = res;
    }
  }
}
`},i=r(`
@group(0) @binding(1) var<storage, read> w: array<vec4f>;
fn weightQuad(i: u32) -> vec4f { return w[i]; }`),a=r(`
@group(0) @binding(1) var<storage, read> w: array<vec2u>; // four fp16 columns per element
fn weightQuad(i: u32) -> vec4f { let q = w[i]; return vec4f(unpack2x16float(q.x), unpack2x16float(q.y)); }`),o=`
@group(0) @binding(1) var<storage, read> w: array<vec4h>;
fn weightQuad(i: u32) -> vec4h { return w[i]; }`,s=r(o,`storage`),c=r(o,`math`),l=e=>`
${e?`enable f16;`:``}
struct P { n: u32, heads: u32, width: u32, dmodel: u32, seqStride: u32, batchStride: u32 }
@group(0) @binding(0) var<storage, read> qkv: array<f32>;
@group(0) @binding(1) var<storage, read_write> o: array<f32>; // [rows, dmodel]
@group(0) @binding(2) var<uniform> p: P;
const TILE = 32u;
var<workgroup> ks: array<${e?.k?`vec4h`:`vec4f`}, 256>; // [TILE keys][8 x vec4]
var<workgroup> vs: array<${e?`vec4h`:`vec4f`}, 256>;
@compute @workgroup_size(64)
fn main(@builtin(workgroup_id) wg: vec3u, @builtin(local_invocation_id) l: vec3u) {
  let b = wg.z;
  let h = wg.y;
  let qi = wg.x * 64u + l.x;
  let valid = qi < p.n;
  let qrow = b * p.batchStride + min(qi, p.n - 1u) * p.seqStride;
  var q: array<vec4f, 8>;
  var acc: array<vec4f, 8>;
  let qBase = qrow * p.width + h * 32u;
  for (var d = 0u; d < 8u; d++) {
    q[d] = vec4f(qkv[qBase + 4u * d], qkv[qBase + 4u * d + 1u], qkv[qBase + 4u * d + 2u], qkv[qBase + 4u * d + 3u]) * 0.17677669529663687;
    acc[d] = vec4f(0.0);
  }
  var m = -3.0e38;
  var sum = 0.0;
  for (var k0 = 0u; k0 < p.n; k0 += TILE) {
    // 32 keys x 8 vec4 = 256 vec4 each for K and V, four per invocation.
    for (var i = 0u; i < 4u; i++) {
      let idx = l.x + i * 64u;
      let kj = k0 + idx / 8u;
      var kv = vec4f(0.0);
      var vv = vec4f(0.0);
      if (kj < p.n) {
        let base = (b * p.batchStride + kj * p.seqStride) * p.width + h * 32u + (idx % 8u) * 4u;
        let kb = base + p.dmodel;
        let vb = base + 2u * p.dmodel;
        kv = vec4f(qkv[kb], qkv[kb + 1u], qkv[kb + 2u], qkv[kb + 3u]);
        vv = vec4f(qkv[vb], qkv[vb + 1u], qkv[vb + 2u], qkv[vb + 3u]);
      }
      ks[idx] = ${e?.k?`vec4h(kv)`:`kv`};
      vs[idx] = ${e?`vec4h(vv)`:`vv`};
    }
    workgroupBarrier();
    // Constant trip counts so the compiler unrolls and keeps s[] in registers; keys past the end
    // of the sequence get a score of -3e38, whose exp is exactly 0.
    var s: array<f32, 32>;
    var tileMax = -3.0e38;
    for (var j = 0u; j < TILE; j++) {
      var dotv = vec4f(0.0);
      for (var d = 0u; d < 8u; d++) { dotv = fma(q[d], ${e?.k?`vec4f(ks[j * 8u + d])`:`ks[j * 8u + d]`}, dotv); }
      s[j] = select(-3.0e38, dotv.x + dotv.y + dotv.z + dotv.w, k0 + j < p.n);
      tileMax = max(tileMax, s[j]);
    }
    let mNew = max(m, tileMax);
    let r = exp(m - mNew);
    sum *= r;
    for (var d = 0u; d < 8u; d++) { acc[d] *= r; }
    m = mNew;
${e?.pv?`    var part: array<vec4h, 8>;
    for (var j = 0u; j < TILE; j++) {
      let e = exp(s[j] - m);
      sum += e;
      let eh = vec4h(f16(e));
      for (var d = 0u; d < 8u; d++) { part[d] = fma(eh, vs[j * 8u + d], part[d]); }
    }
    for (var d = 0u; d < 8u; d++) { acc[d] += vec4f(part[d]); }`:`    for (var j = 0u; j < TILE; j++) {
      let e = exp(s[j] - m);
      sum += e;
      for (var d = 0u; d < 8u; d++) { acc[d] = fma(vec4f(e), ${e?`vec4f(vs[j * 8u + d])`:`vs[j * 8u + d]`}, acc[d]); }
    }`}
    workgroupBarrier();
  }
  if (!valid) { return; }
  let gate = 1.0 / (1.0 + exp(-qkv[qrow * p.width + 3u * p.dmodel + h]));
  let scale = gate / sum;
  let oBase = qrow * p.dmodel + h * 32u;
  for (var d = 0u; d < 8u; d++) {
    let v = acc[d] * scale;
    o[oBase + 4u * d] = v.x;
    o[oBase + 4u * d + 1u] = v.y;
    o[oBase + 4u * d + 2u] = v.z;
    o[oBase + 4u * d + 3u] = v.w;
  }
}
`,u=l(null),d=l({k:!0,pv:!1}),f=l({k:!0,pv:!0}),p=`
struct P { freqs: u32, frames: u32, channels: u32 }
@group(0) @binding(0) var<storage, read> x: array<f32>;
@group(0) @binding(1) var<storage, read_write> y: array<f32>;
@group(0) @binding(2) var<uniform> p: P;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3u) {
  let i = id.x;
  if (i >= p.freqs * p.frames * p.channels) { return; }
  let c = i % p.channels;
  let t = (i / p.channels) % p.frames;
  let f = i / (p.channels * p.frames);
  y[(t * p.freqs + f) * p.channels + c] = x[i];
}
`,m=`
struct P { frames: u32 }
@group(0) @binding(0) var<storage, read> l: array<f32>;
@group(0) @binding(1) var<storage, read_write> out: array<f32>;
@group(0) @binding(2) var<uniform> p: P;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3u) {
  let t = id.x;
  if (t >= p.frames) { return; }
  out[t] = l[t * 2u] + l[t * 2u + 1u];
  out[p.frames + t] = l[t * 2u + 1u];
}
`,h={sliceMs:1/0,inFlight:1},g={sliceMs:2,inFlight:1},_=(e,t=h)=>{let n=Math.max(1,Math.floor(t.inFlight)),r=[],i=[],a=0,o=0,s=0;return{async submit(c,l=!1){let u=performance.now();e.submit([c]),o+=1,a+=1;let d=e.onSubmittedWorkDone().then(()=>{--a,i.push(performance.now()-u)});d.catch(()=>void 0),r.push(d);try{if(l){await Promise.all(r.splice(0));return}for(n>1&&t.frame?await Promise.race([d,t.frame()]):n===1&&await d;a>=n;)await r.shift()}finally{s+=performance.now()-u}},get slices(){return o},get waitedMs(){return s},get roundTrips(){return i}}},v=(e,t)=>{let n=/* @__PURE__ */ new Set,r=0;for(let i=0;i<e.length;i+=1){let a=e[i];r>0&&r+a>t&&(n.add(i),r=0),r+=a}return n};var y=class{constructor(e){this.dispatches=e,this.sums=null,this.runs=0,this.broken=!1,this.cuts=null}get wanted(){return!this.broken&&this.runs<2}get costs(){return this.broken||this.runs===0||!this.sums?null:this.sums.map(e=>e/this.runs)}get unusable(){return this.broken}record(e){if(this.broken||this.runs>=2)return;let t=0,n=e.length===this.dispatches;for(let r=0;n&&r<e.length;r+=1){let i=e[r];(!Number.isFinite(i)||i<0)&&(n=!1),t+=i}if(!n||t<=0)this.runs===0&&(this.broken=!0);else{this.sums??=new Float64Array(this.dispatches);for(let t=0;t<e.length;t+=1)this.sums[t]+=e[t];this.runs+=1,this.cuts=null}}giveUp(){this.broken=!0}cutsFor(e){let t=this.costs;return t?(this.cuts?.sliceMs!==e&&(this.cuts={sliceMs:e,at:v(t,e)}),this.cuts.at):null}};const b=(e,t)=>{let n=new Float64Array(t);for(let r=0;r<t;r+=1)n[r]=Number(e[r*2+1]-e[r*2])/1e6;return n},x=e=>{if(!e.length)return 0;let t=[...e].sort((e,t)=>e-t);return t[t.length>>1]},ee=(e,t)=>{let n=0,r=0;for(let t of e)n+=t,r=Math.max(r,t);return{dispatches:e.length,totalMs:n,longestMs:r,slices:v(e,t).size+1}},S=e=>{let t=null,n=0,r=[];return{next:()=>{let i=e;return i?(t??=new Promise(a=>{try{i(e=>{n&&e>n&&(r.push(e-n),r.length>32&&r.shift()),n=e,t=null,a()})}catch{e=void 0}}),t):new Promise(()=>void 0)},get frameMs(){return r.length?Math.min(...r):null}}},C=e=>Math.min(4,Math.max(1,(e&&e>0?e:1e3/120)/4)),w=1500,T=e=>e.reduce((e,t)=>e*t,1),E=/* @__PURE__ */ new WeakMap,D=/* @__PURE__ */ new Map([[t,`stem_bn`],[n,`conv`],[i,`matmul`],[a,`matmul`],[s,`matmul`],[c,`matmul`],[u,`attention`],[d,`attention`],[f,`attention`],[p,`flatten`],[m,`sum_head`]]);var O=class e{constructor(e,t,n,r,i,a){this.device=e,this.build=t,this.melBuffer=n,this.outBuffer=r,this.owned=i,this.variant=a,this.graphs=/* @__PURE__ */ new Map,this.reported=!1}get bytes(){let e=this.owned.reduce((e,t)=>e+t.size,0);for(let t of this.graphs.values())e+=t.owned.reduce((e,t)=>e+t.size,0);return e}graph(e){let t=this.graphs.get(e);if(!t){for(let[e,t]of this.graphs)e!==1500&&(t.owned.forEach(e=>e.destroy()),this.graphs.delete(e));let n=this.build(e),r=n.steps.filter(e=>e.kind===`dispatch`).length,i=new y(r);if(e===1500){let e=E.get(this.device);e||E.set(this.device,e=/* @__PURE__ */ new Map);let t=e.get(this.variant);t?i=t:e.set(this.variant,i)}t={...n,dispatches:r,costs:i},this.graphs.set(e,t)}return t}static async create(r,o,l,h={}){if(h.f16&&!r.features.has(`shader-f16`))throw Error(`beat_this f16: device lacks shader-f16`);let g=w,_=[],v=(e,t=0)=>{let n=r.createBuffer({size:Math.max(16,e*4),usage:GPUBufferUsage.STORAGE|GPUBufferUsage.COPY_SRC|GPUBufferUsage.COPY_DST|t});return _.push(n),n},y=v(Math.ceil(o.byteLength/4));r.queue.writeBuffer(y,0,o.buffer,o.byteOffset,o.byteLength);let b=e=>{let t=l.tensors[e];if(!t)throw Error(`beat_this weights: missing tensor ${e}`);return t},x=e=>{let{byte_offset:t,shape:n,dtype:r,stride:i}=b(e),a=i?n[0]*i:T(n);return{buffer:y,offset:t,size:a*(r===`f16`?2:4)}},ee=e=>l.tensors[e].shape,S=l.hyper_parameters.transformer_dim,C=l.hyper_parameters.stem_dim,E=l.hyper_parameters.ff_mult,O=32*g,k=e=>3*e+e/32,A=v(g*128),j=v(g*128),M=[v(O*C),v(O*C)],N=v(Math.max(O*k(C),g*k(S))),P=v(O*C),F=v(Math.max(O*C*E,g*S*E)),I=v(g*4*C*8),L=v(g*2),R=v(g*2),te=v(4096),z=o.byteOffset+b(`rope.freqs`).byte_offset,B=new Float32Array(o.buffer.slice(z,z+64)),ne=new Float32Array(g*16*2);for(let e=0;e<g;e+=1)for(let t=0;t<16;t+=1){let n=Math.fround(Math.fround(e)*B[t]);ne[(e*16+t)*2]=Math.cos(n),ne[(e*16+t)*2+1]=Math.sin(n)}let re=v(ne.length);r.queue.writeBuffer(re,0,ne);let ie=/* @__PURE__ */ new Map,ae=e=>{let t=ie.get(e);return t||(t=r.createComputePipeline({layout:`auto`,compute:{module:r.createShaderModule({code:e}),entryPoint:`main`}}),ie.set(e,t)),t},oe=new e(r,e=>{let o=[],g=e=>{let t=new Uint32Array(Math.ceil(e.length/4)*4);t.set(e);let n=r.createBuffer({size:t.byteLength,usage:GPUBufferUsage.UNIFORM|GPUBufferUsage.COPY_DST});return r.queue.writeBuffer(n,0,t),o.push(n),n},_=[],v=`stem`,y=(e,t,n,i,a=``)=>{let o=ae(e),s=[...t,{buffer:g(n)}].map((e,t)=>({binding:t,resource:e})),c=`${v} ${D.get(e)}${a?` ${a}`:``}`;_.push({kind:`dispatch`,label:c,pipeline:o,group:r.createBindGroup({layout:o.getBindGroupLayout(0),entries:s}),count:i})},w=e=>[Math.ceil(e/64),1,1],T=(e,t,n)=>_.push({kind:`stage`,name:e,buffer:t,floats:n}),E=(e,t,n,r,o,l,u={posDiv:1,posMod:1,dmodel:0})=>{let[d,f]=ee(t),{dtype:p,stride:m}=b(t);if(!m)throw Error(`beat_this weights: ${t} has no row stride (re-export the weights)`);let g=h.f16===`math`?c:s;y(p===`f16`?h.f16?g:a:i,[{buffer:e},x(t),n?x(n):{buffer:te},{buffer:r},{buffer:re}],[o,f,d,l,u.posDiv,u.posMod,u.dmodel,m],[Math.ceil(f/64),Math.ceil(o/64),1],t.split(`.`).slice(-2).join(`.`))},O=(e,t,n,r,i)=>{let a=r/32,o=3*r+a;v=t,E(e,`${t}.attn.qkvg.w`,`${t}.attn.qkvg.b`,N,n,12,{posDiv:i.posDiv,posMod:i.n,dmodel:r}),y(h.f16?h.f16===`math`?f:d:u,[{buffer:N},{buffer:P}],[i.n,a,o,r,i.seqStride,i.batchStride],[Math.ceil(i.n/64),a,i.batches]),E(P,`${t}.attn.out.w`,null,e,n,2),E(e,`${t}.ff.w1`,`${t}.ff.b1`,F,n,5),E(F,`${t}.ff.w2`,`${t}.ff.b2`,e,n,2)};y(t,[{buffer:A},x(`stem.bn.scale`),x(`stem.bn.shift`),{buffer:j}],[e,128],w(e*128));let k=32,z=C;y(n,[{buffer:j},x(`stem.conv.w`),x(`stem.conv.b`),{buffer:M[0]}],[k,e,1,z,4],[Math.ceil(z/64),Math.ceil(k*e/64),1]);let B=0;T(`stem`,M[B],k*e*z);for(let t=0;t<3;t+=1){let r=k*e;O(M[B],`block${t}.freq`,r,z,{n:k,seqStride:e,batchStride:1,batches:e,posDiv:e}),T(`block${t}_freq`,M[B],r*z),O(M[B],`block${t}.time`,r,z,{n:e,seqStride:1,batchStride:e,batches:k,posDiv:1}),T(`block${t}_time`,M[B],r*z);let i=1-B;v=`block${t}`,y(n,[{buffer:M[B]},x(`block${t}.conv.w`),x(`block${t}.conv.b`),{buffer:M[i]}],[k/2,e,z,z*2,2],[Math.ceil(z*2/64),Math.ceil(k/2*e/64),1]),B=i,k/=2,z*=2,T(`block${t}_conv`,M[B],k*e*z)}v=`frontend`,y(p,[{buffer:M[B]},{buffer:I}],[k,e,z],w(k*e*z)),B=1-B,E(I,`frontend.linear.w`,`frontend.linear.b`,M[B],e,0),T(`frontend`,M[B],e*S);for(let t=0;t<l.hyper_parameters.n_layers;t+=1)O(M[B],`layer${t}`,e,S,{n:e,seqStride:1,batchStride:e,batches:1,posDiv:1}),T(`layer${t}`,M[B],e*S);return v=`head`,E(M[B],`head.w`,`head.b`,L,e,4),y(m,[{buffer:L},{buffer:R}],[e],w(e)),T(`logits`,R,e*2),{steps:_,owned:o}},A,R,_,h.f16??`f32`);return oe.graph(g),await r.queue.onSubmittedWorkDone(),oe}async run(e,t={}){let n=e.length/128;if(!Number.isInteger(n)||n<1||n>1500)throw Error(`beat_this chunk must be 1..${w} frames x 128, got ${e.length} values`);let r=this.graph(n),{steps:i}=r,{device:a}=this;a.queue.writeBuffer(this.melBuffer,0,e.buffer,e.byteOffset,e.byteLength);let o=t.slice??`none`,s=o===`paced`,c=s?t.pacing??g:{sliceMs:1/0,inFlight:Math.max(1,t.inFlight??1)},l=a.features.has(`timestamp-query`)&&r.dispatches*2<=4096,u=!1;s&&r.costs.wanted&&!l&&(r.costs.giveUp(),u=!0);let d=l&&(t.profile||s&&r.costs.wanted)?a.createQuerySet({type:`timestamp`,count:r.dispatches*2}):null,f=s?r.costs.cutsFor(c.sliceMs):null,p=_(a.queue,s&&!f?{...c,inFlight:1}:c),m=s?void 0:t.between,h=a.createCommandEncoder(),v=[],y=null,S=!1,C=async(e=!1)=>{y?.end(),y=null;let t=h.finish();h=a.createCommandEncoder(),S=!1,await p.submit(t,e)},T=0,E=performance.now();for(let e of i){if(o===`dispatch`&&y&&(await C(),await m?.()),e.kind===`dispatch`){f?.has(T)&&S&&await C(),d&&(y?.end(),y=h.beginComputePass({timestampWrites:{querySet:d,beginningOfPassWriteIndex:T*2,endOfPassWriteIndex:T*2+1}})),y??=h.beginComputePass(),y.setPipeline(e.pipeline),y.setBindGroup(0,e.group),y.dispatchWorkgroups(...e.count),T+=1,S=!0;continue}if((o===`stage`||s&&!f)&&e.name!==`logits`&&S&&(await C(),await m?.()),!t.stages&&e.name!==`logits`)continue;y?.end(),y=null;let n=a.createBuffer({size:e.floats*4,usage:GPUBufferUsage.MAP_READ|GPUBufferUsage.COPY_DST});h.copyBufferToBuffer(e.buffer,0,n,0,e.floats*4),v.push({name:e.name,buffer:n})}y?.end(),y=null;let D=null,O=null;if(d){let e=r.dispatches*16;O=a.createBuffer({size:e,usage:GPUBufferUsage.QUERY_RESOLVE|GPUBufferUsage.COPY_SRC}),D=a.createBuffer({size:e,usage:GPUBufferUsage.MAP_READ|GPUBufferUsage.COPY_DST}),h.resolveQuerySet(d,0,r.dispatches*2,O,0),h.copyBufferToBuffer(O,0,D,0,e)}await C(!0);let k=performance.now()-E;O?.destroy(),d?.destroy();let A={};for(let{name:e,buffer:t}of v)await t.mapAsync(GPUMapMode.READ),A[e]=new Float32Array(t.getMappedRange().slice(0)),t.destroy();let j,M;if(D){await D.mapAsync(GPUMapMode.READ);let e=b(new BigInt64Array(D.getMappedRange()),r.dispatches);D.destroy(),t.profile&&(j=i.filter(e=>e.kind===`dispatch`).map((t,n)=>({label:t.label,ms:e[n]}))),r.costs.record(e)}let N=s&&n===1500?r.costs.costs:null;N&&!this.reported&&(M=ee(N,c.sliceMs),this.reported=!0),s&&r.costs.unusable&&(u=!0);let P=A.logits;return{beat:P.subarray(0,n),downbeat:P.subarray(n,2*n),gpuMs:p.waitedMs,wallMs:k,slices:p.slices,...s?{paced:{slices:p.slices,sliceMs:c.sliceMs,byCosts:f!==null,inFlight:f?c.inFlight:1,pacedInFlight:c.inFlight,roundTripMs:x(p.roundTrips),...M?{costs:M}:{},...u?{noTimestamps:u}:{}}}:{},...t.stages?{stages:A}:{},...j?{profile:j}:{}}}destroy(){this.owned.forEach(e=>e.destroy()),this.graphs.forEach(e=>e.owned.forEach(e=>e.destroy()))}};const k=1024,A=/* @__PURE__ */ new WeakMap,j=async(e,t,n,r=h)=>{if(n<1||t.length<n*128)throw Error(`percussive: ${t.length} floats for ${n} frames`);let i=A.get(e);i||(i=await e.createComputePipelineAsync({layout:`auto`,compute:{module:e.createShaderModule({code:`
struct Params { frames: u32, first: u32, count: u32 }
@group(0) @binding(0) var<storage, read> mel: array<f32>;
@group(0) @binding(1) var<storage, read_write> energy: array<f32>;
@group(0) @binding(2) var<uniform> p: Params;

const BANDS: i32 = 128;
const HALF: i32 = 8;
const TAPS: i32 = 17;

fn magnitude(frame: i32, band: i32) -> f32 {
  if (frame < 0 || frame >= i32(p.frames) || band < 0 || band >= BANDS) { return 0.0; }
  let v = mel[u32(frame * BANDS + band)];
  // expm1 without its cancellation near 0.
  if (v < 1e-3) { return max(0.0, v + 0.5 * v * v); }
  return exp(v) - 1.0;
}

fn median(values: ptr<function, array<f32, 17>>) -> f32 {
  for (var i = 1; i < TAPS; i++) {
    let value = (*values)[i];
    var j = i - 1;
    while (j >= 0 && (*values)[j] > value) {
      (*values)[j + 1] = (*values)[j];
      j--;
    }
    (*values)[j + 1] = value;
  }
  return (*values)[HALF];
}

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3<u32>) {
  if (id.x >= p.count) { return; }
  let at = p.first + id.x;
  let frame = i32(at);
  var percussive = 0.0;
  var total = 0.0;
  var taps: array<f32, 17>;
  for (var band = 0; band < BANDS; band++) {
    for (var k = 0; k < TAPS; k++) { taps[k] = magnitude(frame + k - HALF, band); }
    let harmonic = median(&taps);
    for (var k = 0; k < TAPS; k++) { taps[k] = magnitude(frame, band + k - HALF); }
    let hit = median(&taps);
    let value = magnitude(frame, band);
    let e = value * value;
    let hh = harmonic * harmonic;
    let pp = hit * hit;
    if (hh + pp > 0.0) { percussive += e * pp / (hh + pp); }
    total += e;
  }
  energy[2u * at] = percussive;
  energy[2u * at + 1u] = total;
}
`}),entryPoint:`main`}}),A.set(e,i));let a=e.createBuffer({size:n*128*4,usage:GPUBufferUsage.STORAGE|GPUBufferUsage.COPY_DST}),o=e.createBuffer({size:n*2*4,usage:GPUBufferUsage.STORAGE|GPUBufferUsage.COPY_SRC}),s=Math.ceil(n/k),c=e.createBuffer({size:s*256,usage:GPUBufferUsage.UNIFORM|GPUBufferUsage.COPY_DST}),l=e.createBuffer({size:n*2*4,usage:GPUBufferUsage.MAP_READ|GPUBufferUsage.COPY_DST});try{e.queue.writeBuffer(a,0,t.buffer,t.byteOffset,n*128*4);let u=_(e.queue,r);for(let t=0;t<s;t+=1){let r=t*k,d=Math.min(k,n-r);e.queue.writeBuffer(c,t*256,new Uint32Array([n,r,d,0]));let f=e.createBindGroup({layout:i.getBindGroupLayout(0),entries:[{binding:0,resource:{buffer:a}},{binding:1,resource:{buffer:o}},{binding:2,resource:{buffer:c,offset:t*256,size:16}}]}),p=e.createCommandEncoder(),m=p.beginComputePass();m.setPipeline(i),m.setBindGroup(0,f),m.dispatchWorkgroups(Math.ceil(d/64)),m.end(),t===s-1&&p.copyBufferToBuffer(o,0,l,0,n*2*4),await u.submit(p.finish(),t===s-1)}await l.mapAsync(GPUMapMode.READ);let d=new Float32Array(l.getMappedRange().slice(0));l.unmap();let f=new Float32Array(n),p=new Float32Array(n);for(let e=0;e<n;e+=1)f[e]=d[2*e],p[e]=d[2*e+1];return{percussive:f,total:p}}finally{a.destroy(),o.destroy(),c.destroy(),l.destroy()}},M=`
fn erf_f32(v: f32) -> f32 {
  let a = abs(v);
  let x = 1.0 / (1.0 + 0.3275911 * a);
  let y = 1.0 - ((((1.061405429 * x - 1.453152027) * x + 1.421413741) * x - 0.284496736) * x + 0.254829592) * x * exp(-a * a);
  return select(-y, y, v >= 0.0);
}
fn gelu(v: f32) -> f32 { return 0.5 * v * (1.0 + erf_f32(v * 0.7071067811865476)); }
`,N=e=>e?`
@group(0) @binding(1) var<storage, read> w: array<u32>;
fn wload(e: u32) -> f32 {
  let pair = unpack2x16float(w[e >> 1u]);
  return select(pair.x, pair.y, (e & 1u) == 1u);
}`:`
@group(0) @binding(1) var<storage, read> w: array<f32>;
fn wload(e: u32) -> f32 { return w[e]; }`,P=e=>`
var<workgroup> as_: array<${e===`f16`?`vec4h`:`vec4f`}, 256>;
var<workgroup> bs_: array<${e===`f16`?`vec4h`:`vec4f`}, 256>;`,F=(e,t)=>`${e===`f16`?`vec4h`:`vec4f`}(${t})`,I=e=>e===`f16`?`
    var part: array<vec4h, 4>;
    for (var kk = 0u; kk < 16u; kk++) {
      let a = as_[kk * 16u + l.y];
      let bb = bs_[kk * 16u + l.x];
      for (var i = 0u; i < 4u; i++) { part[i] = fma(vec4h(a[i]), bb, part[i]); }
    }
    for (var i = 0u; i < 4u; i++) { acc[i] += vec4f(part[i]); }`:`
    for (var kk = 0u; kk < 16u; kk++) {
      let a = as_[kk * 16u + l.y];
      let bb = bs_[kk * 16u + l.x];
      for (var i = 0u; i < 4u; i++) { acc[i] = fma(vec4f(a[i]), bb, acc[i]); }
    }`,L=e=>e===`f16`?`enable f16;`:``,R=(e,t)=>`
${P(t)}
@compute @workgroup_size(16, 16)
fn main(@builtin(workgroup_id) wg: vec3u, @builtin(local_invocation_id) l: vec3u, @builtin(local_invocation_index) li: u32) {
  let n = ${e};
  let row0 = wg.y * 64u;
  let col0 = wg.x * 64u;
  var acc: array<vec4f, 4>;
  for (var k0 = 0u; k0 < p.k; k0 += 16u) {
    // Invocation li: rows 4 (li / 16).. at k li % 16 of A, columns 4 (li % 16).. at k li / 16 of B.
    let ak = k0 + li % 16u;
    var av = vec4f(0.0);
    let bk = k0 + li / 16u;
    var bv = vec4f(0.0);
    for (var c = 0u; c < 4u; c++) {
      let ar = row0 + (li / 16u) * 4u + c;
      if (ar < p.m && ak < p.k) { av[c] = wload(ar * p.k + ak); }
      let bc = col0 + (li % 16u) * 4u + c;
      if (bk < p.k && bc < n) { bv[c] = gather(bk, bc); }
    }
    as_[(li % 16u) * 16u + li / 16u] = ${F(t,`av`)};
    bs_[li] = ${F(t,`bv`)};
    workgroupBarrier();${I(t)}
    workgroupBarrier();
  }
  for (var i = 0u; i < 4u; i++) {
    let r = row0 + l.y * 4u + i;
    if (r >= p.m) { continue; }
    for (var j = 0u; j < 4u; j++) {
      let c = col0 + l.x * 4u + j;
      if (c >= n) { continue; }
      store(r, c, acc[i][j]);
    }
  }
}
`,te=(e,t=`f32`)=>`
${L(t)}
${M}
struct P { m: u32, k: u32, cin: u32, kh: u32, kw: u32, sh: u32, sw: u32, ph: u32, pw: u32, dh: u32, dw: u32, hin: u32, win: u32, hout: u32, wout: u32, flags: u32 }
@group(0) @binding(0) var<storage, read> x: array<f32>;
${N(e)}
@group(0) @binding(2) var<storage, read> b: array<f32>;
@group(0) @binding(3) var<storage, read> side: array<f32>;
@group(0) @binding(4) var<storage, read_write> y: array<f32>;
@group(0) @binding(5) var<uniform> p: P;

fn gather(k: u32, n: u32) -> f32 {
  let taps = p.kh * p.kw;
  let ci = k / taps;
  let r = k - ci * taps;
  let a = r / p.kw;
  let c = r - a * p.kw;
  let ho = n / p.wout;
  let wo = n - ho * p.wout;
  let hi = i32(ho * p.sh + a * p.dh) - i32(p.ph);
  let wi = i32(wo * p.sw + c * p.dw) - i32(p.pw);
  if (hi < 0 || hi >= i32(p.hin) || wi < 0 || wi >= i32(p.win)) { return 0.0; }
  return x[(ci * p.hin + u32(hi)) * p.win + u32(wi)];
}

fn store(r: u32, c: u32, sum: f32) {
  var v = sum + b[r];
  if ((p.flags & 1u) != 0u) { v = gelu(v); }
  let o = r * p.hout * p.wout + c;
  if ((p.flags & 2u) != 0u) { y[o] = y[o] + side[r] * v; } else { y[o] = v; }
}
${R(`p.hout * p.wout`,t)}
`,z=(e,t=`f32`)=>`
${L(t)}
${M}
struct P { m: u32, k: u32, cin: u32, hin: u32, win: u32, axis: u32, flags: u32 }
@group(0) @binding(0) var<storage, read> x: array<f32>;
${N(e)}
@group(0) @binding(2) var<storage, read> b: array<f32>;
@group(0) @binding(3) var<storage, read_write> y: array<f32>;
@group(0) @binding(4) var<uniform> p: P;

fn gather(k: u32, n: u32) -> f32 {
  let ci = k / 2u;
  let t = k - ci * 2u;
  if (p.axis == 0u) {
    let q = n / p.win;
    let w = n - q * p.win;
    if (q < t || q - t >= p.hin) { return 0.0; }
    return x[(ci * p.hin + q - t) * p.win + w];
  }
  let h = n / (p.win + 1u);
  let q = n - h * (p.win + 1u);
  if (q < t || q - t >= p.win) { return 0.0; }
  return x[(ci * p.hin + h) * p.win + q - t];
}

fn store(r: u32, c: u32, sum: f32) {
  let co = r / 4u;
  let ph = r - co * 4u;
  var v = sum + b[co];
  if ((p.flags & 1u) != 0u) { v = gelu(v); }
  if (p.axis == 0u) {
    let q = c / p.win;
    let w = c - q * p.win;
    let o = 4u * q + ph;
    if (o < 2u || o - 2u >= 4u * p.hin) { return; }
    y[(co * 4u * p.hin + o - 2u) * p.win + w] = v;
  } else {
    let h = c / (p.win + 1u);
    let q = c - h * (p.win + 1u);
    let o = 4u * q + ph;
    if (o < 2u || o - 2u >= 4u * p.win) { return; }
    y[(co * p.hin + h) * 4u * p.win + o - 2u] = v;
  }
}
${R(`select(p.hin * (p.win + 1u), (p.hin + 1u) * p.win, p.axis == 0u)`,t)}
`,B=`
struct P { parts: u32, c: u32, inner: u32, cs: u32, gs: u32, mode: u32 }
@group(0) @binding(0) var<storage, read> x: array<f32>;
@group(0) @binding(1) var<storage, read> stats: array<f32>;
@group(0) @binding(2) var<storage, read_write> partial: array<f32>;
@group(0) @binding(3) var<uniform> p: P;
var<workgroup> sums: array<f32, 256>;
@compute @workgroup_size(256)
fn main(@builtin(workgroup_id) wg: vec3u, @builtin(local_invocation_index) li: u32) {
  let g = wg.y;
  let n = p.c * p.inner;
  let chunk = (n + p.parts - 1u) / p.parts;
  let lo = wg.x * chunk;
  let hi = min(n, lo + chunk);
  let mean = stats[g * 4u];
  var s = 0.0;
  for (var e = lo + li; e < hi; e += 256u) {
    let ch = e / p.inner;
    let v = x[ch * p.cs + g * p.gs + e - ch * p.inner];
    if (p.mode == 0u) { s += v; } else { let d = v - mean; s = fma(d, d, s); }
  }
  sums[li] = s;
  workgroupBarrier();
  for (var span = 128u; span > 0u; span >>= 1u) {
    if (li < span) { sums[li] += sums[li + span]; }
    workgroupBarrier();
  }
  if (li == 0u) { partial[g * p.parts + wg.x] = sums[0]; }
}
`,ne=`
struct P { parts: u32, n: u32, mode: u32 }
@group(0) @binding(0) var<storage, read> partial: array<f32>;
@group(0) @binding(1) var<storage, read_write> stats: array<f32>;
@group(0) @binding(2) var<uniform> p: P;
var<workgroup> sums: array<f32, 256>;
@compute @workgroup_size(256)
fn main(@builtin(workgroup_id) wg: vec3u, @builtin(local_invocation_index) li: u32) {
  let g = wg.x;
  var s = 0.0;
  for (var i = li; i < p.parts; i += 256u) { s += partial[g * p.parts + i]; }
  sums[li] = s;
  workgroupBarrier();
  for (var span = 128u; span > 0u; span >>= 1u) {
    if (li < span) { sums[li] += sums[li + span]; }
    workgroupBarrier();
  }
  if (li != 0u) { return; }
  let total = sums[0];
  let n = f32(p.n);
  if (p.mode == 0u) {
    stats[g * 4u] = total / n;
  } else if (p.mode == 1u) {
    stats[g * 4u + 1u] = 1.0 / sqrt(total / n + 1e-5);
  } else {
    let sd = sqrt(total / (n - 1.0));
    stats[g * 4u + 1u] = 1.0 / (sd + 1e-5);
    stats[g * 4u + 2u] = sd;
  }
}
`,re=`
${M}
struct P { c: u32, h: u32, w: u32, perH: u32, gelu: u32 }
@group(0) @binding(0) var<storage, read_write> x: array<f32>;
@group(0) @binding(1) var<storage, read> stats: array<f32>;
@group(0) @binding(2) var<storage, read> gamma: array<f32>;
@group(0) @binding(3) var<storage, read> beta: array<f32>;
@group(0) @binding(4) var<uniform> p: P;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) id: vec3u, @builtin(num_workgroups) groups: vec3u) {
  let i = id.x + id.y * groups.x * 256u;
  let plane = p.h * p.w;
  if (i >= p.c * plane) { return; }
  let ch = i / plane;
  let g = select(0u, (i - ch * plane) / p.w, p.perH == 1u);
  var v = (x[i] - stats[g * 4u]) * stats[g * 4u + 1u] * gamma[ch] + beta[ch];
  if (p.gelu == 1u) { v = gelu(v); }
  x[i] = v;
}
`,ie=`
struct P { c: u32, h: u32, w: u32, perH: u32 }
@group(0) @binding(0) var<storage, read> x: array<f32>;
@group(0) @binding(1) var<storage, read> stats: array<f32>;
@group(0) @binding(2) var<storage, read> gamma: array<f32>;
@group(0) @binding(3) var<storage, read> beta: array<f32>;
@group(0) @binding(4) var<storage, read> scale: array<f32>;
@group(0) @binding(5) var<storage, read_write> res: array<f32>;
@group(0) @binding(6) var<uniform> p: P;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) id: vec3u, @builtin(num_workgroups) groups: vec3u) {
  let i = id.x + id.y * groups.x * 256u;
  let plane = p.h * p.w;
  if (i >= p.c * plane) { return; }
  let ch = i / plane;
  let at = i - ch * plane;
  let g = select(0u, at / p.w, p.perH == 1u);
  let mean = stats[g * 4u];
  let rstd = stats[g * 4u + 1u];
  let a = (x[i] - mean) * rstd * gamma[ch] + beta[ch];
  let gch = ch + p.c;
  let gate = (x[gch * plane + at] - mean) * rstd * gamma[gch] + beta[gch];
  res[i] = res[i] + scale[ch] * (a / (1.0 + exp(-gate)));
}
`,ae=`
struct P { c: u32, h: u32, w: u32, emb: u32 }
@group(0) @binding(0) var<storage, read> x: array<f32>;
@group(0) @binding(1) var<storage, read> emb: array<f32>;
@group(0) @binding(2) var<storage, read_write> y: array<f32>;
@group(0) @binding(3) var<uniform> p: P;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) id: vec3u, @builtin(num_workgroups) groups: vec3u) {
  let i = id.x + id.y * groups.x * 256u;
  let plane = p.h * p.w;
  if (i >= p.c * plane) { return; }
  var v = x[i] / (1.0 + exp(-x[i + p.c * plane]));
  if (p.emb == 1u) {
    let ch = i / plane;
    v += emb[ch * p.h + (i - ch * plane) / p.w];
  }
  y[i] = v;
}
`,oe=`
struct P { n: u32 }
@group(0) @binding(0) var<storage, read_write> x: array<f32>;
@group(0) @binding(1) var<storage, read> y: array<f32>;
@group(0) @binding(2) var<uniform> p: P;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) id: vec3u, @builtin(num_workgroups) groups: vec3u) {
  let i = id.x + id.y * groups.x * 256u;
  if (i < p.n) { x[i] = x[i] + y[i]; }
}
`,se=`
struct P { n: u32 }
@group(0) @binding(0) var<storage, read_write> x: array<f32>;
@group(0) @binding(1) var<storage, read> stats: array<f32>;
@group(0) @binding(2) var<uniform> p: P;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) id: vec3u, @builtin(num_workgroups) groups: vec3u) {
  let i = id.x + id.y * groups.x * 256u;
  if (i < p.n) { x[i] = (x[i] - stats[0]) * stats[1]; }
}
`,ce=`
struct P { c: u32, n: u32, pos: u32 }
@group(0) @binding(0) var<storage, read> x: array<f32>;
@group(0) @binding(1) var<storage, read> gamma: array<f32>;
@group(0) @binding(2) var<storage, read> beta: array<f32>;
@group(0) @binding(3) var<storage, read> pos: array<f32>;
@group(0) @binding(4) var<storage, read_write> y: array<f32>;
@group(0) @binding(5) var<uniform> p: P;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3u) {
  let t = id.x;
  if (t >= p.n) { return; }
  var s = 0.0;
  for (var ch = 0u; ch < p.c; ch++) { s += x[ch * p.n + t]; }
  let mean = s / f32(p.c);
  var q = 0.0;
  for (var ch = 0u; ch < p.c; ch++) { let d = x[ch * p.n + t] - mean; q = fma(d, d, q); }
  let rstd = 1.0 / sqrt(q / f32(p.c) + 1e-5);
  for (var ch = 0u; ch < p.c; ch++) {
    var v = (x[ch * p.n + t] - mean) * rstd * gamma[ch] + beta[ch];
    if (p.pos == 1u) { v += pos[ch * p.n + t]; }
    y[ch * p.n + t] = v;
  }
}
`,le=(e=`f32`)=>`
${L(e)}
struct P { nq: u32, nk: u32, qr0: u32, kr0: u32, head0: u32 }
@group(0) @binding(0) var<storage, read> q: array<f32>;
@group(0) @binding(1) var<storage, read> kv: array<f32>;
@group(0) @binding(2) var<storage, read_write> s: array<f32>;
@group(0) @binding(3) var<uniform> p: P;
${P(e)} // [16 d][16 query quads], [16 d][16 key quads]
@compute @workgroup_size(16, 16)
fn main(@builtin(workgroup_id) wg: vec3u, @builtin(local_invocation_id) l: vec3u, @builtin(local_invocation_index) li: u32) {
  let h = p.head0 + wg.z;
  let row0 = wg.y * 64u;
  let col0 = wg.x * 64u;
  var acc: array<vec4f, 4>;
  for (var d0 = 0u; d0 < 64u; d0 += 16u) {
    // Invocation li: queries and keys 4 (li % 16).. at dimension li / 16.
    let d = d0 + li / 16u;
    var av = vec4f(0.0);
    var bv = vec4f(0.0);
    for (var c = 0u; c < 4u; c++) {
      let e = (li % 16u) * 4u + c;
      let i = row0 + e;
      let j = col0 + e;
      av[c] = select(0.0, q[(p.qr0 + h * 64u + d) * p.nq + min(i, p.nq - 1u)], i < p.nq);
      bv[c] = select(0.0, kv[(p.kr0 + h * 64u + d) * p.nk + min(j, p.nk - 1u)], j < p.nk);
    }
    as_[li] = ${F(e,`av`)};
    bs_[li] = ${F(e,`bv`)};
    workgroupBarrier();${I(e)}
    workgroupBarrier();
  }
  for (var i = 0u; i < 4u; i++) {
    let r = row0 + l.y * 4u + i;
    if (r >= p.nq) { continue; }
    for (var j = 0u; j < 4u; j++) {
      let c = col0 + l.x * 4u + j;
      if (c < p.nk) { s[(wg.z * p.nq + r) * p.nk + c] = acc[i][j]; }
    }
  }
}
`,ue=`
struct P { nk: u32 }
@group(0) @binding(0) var<storage, read_write> s: array<f32>;
@group(0) @binding(1) var<uniform> p: P;
var<workgroup> red: array<f32, 256>;
@compute @workgroup_size(256)
fn main(@builtin(workgroup_id) wg: vec3u, @builtin(num_workgroups) groups: vec3u, @builtin(local_invocation_index) li: u32) {
  let base = (wg.x + wg.y * groups.x) * p.nk;
  var m = -3.0e38;
  for (var j = li; j < p.nk; j += 256u) { m = max(m, s[base + j]); }
  red[li] = m;
  workgroupBarrier();
  for (var span = 128u; span > 0u; span >>= 1u) {
    if (li < span) { red[li] = max(red[li], red[li + span]); }
    workgroupBarrier();
  }
  m = red[0];
  workgroupBarrier();
  var sum = 0.0;
  for (var j = li; j < p.nk; j += 256u) {
    let e = exp(s[base + j] - m);
    s[base + j] = e;
    sum += e;
  }
  red[li] = sum;
  workgroupBarrier();
  for (var span = 128u; span > 0u; span >>= 1u) {
    if (li < span) { red[li] += red[li + span]; }
    workgroupBarrier();
  }
  let inv = 1.0 / red[0];
  for (var j = li; j < p.nk; j += 256u) { s[base + j] *= inv; }
}
`,de=(e=`f32`)=>`
${L(e)}
struct P { nq: u32, nk: u32, vr0: u32, head0: u32 }
@group(0) @binding(0) var<storage, read> s: array<f32>;
@group(0) @binding(1) var<storage, read> kv: array<f32>;
@group(0) @binding(2) var<storage, read_write> o: array<f32>;
@group(0) @binding(3) var<uniform> p: P;
${P(e)} // [16 keys][16 query quads], [16 keys][16 d quads]
@compute @workgroup_size(16, 16)
fn main(@builtin(workgroup_id) wg: vec3u, @builtin(local_invocation_id) l: vec3u, @builtin(local_invocation_index) li: u32) {
  let h = p.head0 + wg.z;
  let row0 = wg.y * 64u;
  var acc: array<vec4f, 4>;
  for (var j0 = 0u; j0 < p.nk; j0 += 16u) {
    // Invocation li: queries and dimensions 4 (li / 16).. at key li % 16.
    let jj = li % 16u;
    let j = j0 + jj;
    var a = vec4f(0.0);
    var b = vec4f(0.0);
    for (var c = 0u; c < 4u; c++) {
      let r = (li / 16u) * 4u + c;
      let i = row0 + r;
      if (j < p.nk) {
        if (i < p.nq) { a[c] = s[(wg.z * p.nq + i) * p.nk + j]; }
        b[c] = kv[(p.vr0 + h * 64u + r) * p.nk + j];
      }
    }
    as_[jj * 16u + li / 16u] = ${F(e,`a`)};
    bs_[jj * 16u + li / 16u] = ${F(e,`b`)};
    workgroupBarrier();${I(e)}
    workgroupBarrier();
  }
  for (var i = 0u; i < 4u; i++) {
    let r = row0 + l.y * 4u + i;
    if (r >= p.nq) { continue; }
    for (var j = 0u; j < 4u; j++) {
      let d = l.x * 4u + j;
      o[(h * 64u + d) * p.nq + r] = acc[i][j];
    }
  }
}
`,fe=4096,pe=`
struct P { frames: u32, len: u32 }
@group(0) @binding(0) var<storage, read> mix: array<f32>;      // [2, len]
@group(0) @binding(1) var<storage, read> win: array<f32>;
@group(0) @binding(2) var<storage, read> tw: array<vec2f>;
@group(0) @binding(3) var<storage, read_write> y: array<vec2f>;  // [2 frames, 64 k1, 64 n2]
@group(0) @binding(4) var<uniform> p: P;
fn reflect(i: i32, n: i32) -> i32 {
  var j = select(i, -i, i < 0);
  return select(j, 2 * (n - 1) - j, j >= n);
}
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3u) {
  let n2 = id.x;
  let k1 = id.y;
  let s = id.z;
  if (n2 >= 64u || k1 >= 64u || s >= 2u * p.frames) { return; }
  let ch = s / p.frames;
  let f = s - ch * p.frames;
  let padded = i32(p.len) + 3072;
  var acc = vec2f(0.0);
  for (var n1 = 0u; n1 < 64u; n1++) {
    let n = n1 * 64u + n2;
    let at = reflect(reflect(i32((f + 2u) * 1024u + n) - 2048, padded) - 1536, i32(p.len));
    let v = mix[ch * p.len + u32(at)] * win[n];
    let t = tw[((k1 * n1) % 64u) * 64u];
    acc += v * vec2f(t.x, -t.y);
  }
  let t = tw[k1 * n2];
  y[(s * 64u + k1) * 64u + n2] = vec2f(acc.x * t.x + acc.y * t.y, acc.y * t.x - acc.x * t.y);
}
`,me=`
struct P { frames: u32 }
@group(0) @binding(0) var<storage, read> y: array<vec2f>;
@group(0) @binding(1) var<storage, read> tw: array<vec2f>;
@group(0) @binding(2) var<storage, read_write> spec: array<f32>;
@group(0) @binding(3) var<uniform> p: P;
@compute @workgroup_size(32)
fn main(@builtin(global_invocation_id) id: vec3u) {
  let k2 = id.x;
  let k1 = id.y;
  let s = id.z;
  if (k2 >= 32u || k1 >= 64u || s >= 2u * p.frames) { return; }
  var acc = vec2f(0.0);
  for (var n2 = 0u; n2 < 64u; n2++) {
    let v = y[(s * 64u + k1) * 64u + n2];
    let t = tw[((k2 * n2) % 64u) * 64u];
    acc += vec2f(v.x * t.x + v.y * t.y, v.y * t.x - v.x * t.y);
  }
  let ch = s / p.frames;
  let f = s - ch * p.frames;
  let k = k1 + 64u * k2;
  spec[((ch * 2u) * 2048u + k) * p.frames + f] = acc.x;
  spec[((ch * 2u + 1u) * 2048u + k) * p.frames + f] = acc.y;
}
`,he=`
struct P { frames: u32, source: u32 }
@group(0) @binding(0) var<storage, read> z: array<f32>;     // [16, 2048, frames]
@group(0) @binding(1) var<storage, read> stats: array<f32>;
@group(0) @binding(2) var<storage, read> tw: array<vec2f>;
@group(0) @binding(3) var<storage, read_write> y: array<vec2f>;
@group(0) @binding(4) var<uniform> p: P;
fn bin(ch: u32, f: u32, k: u32) -> vec2f {
  let kk = select(k, 4096u - k, k > 2048u);
  if (kk == 2048u) { return vec2f(0.0); }
  let base = (p.source * 4u + ch * 2u) * 2048u + kk;
  let re = z[base * p.frames + f] * stats[2] + stats[0];
  let im = z[(base + 2048u) * p.frames + f] * stats[2] + stats[0];
  return select(vec2f(re, im), vec2f(re, -im), k > 2048u);
}
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3u) {
  let n2 = id.x;
  let k1 = id.y;
  let s = id.z;
  if (n2 >= 64u || k1 >= 64u || s >= 2u * p.frames) { return; }
  let ch = s / p.frames;
  let f = s - ch * p.frames;
  var acc = vec2f(0.0);
  for (var k2 = 0u; k2 < 64u; k2++) {
    let v = bin(ch, f, k1 + 64u * k2);
    let t = tw[((k2 * n2) % 64u) * 64u];
    acc += vec2f(v.x * t.x - v.y * t.y, v.x * t.y + v.y * t.x);
  }
  let t = tw[k1 * n2];
  y[(s * 64u + k1) * 64u + n2] = vec2f(acc.x * t.x - acc.y * t.y, acc.x * t.y + acc.y * t.x);
}
`,ge=`
struct P { frames: u32 }
@group(0) @binding(0) var<storage, read> y: array<vec2f>;
@group(0) @binding(1) var<storage, read> tw: array<vec2f>;
@group(0) @binding(2) var<storage, read> win: array<f32>;
@group(0) @binding(3) var<storage, read_write> out: array<f32>;  // [2 frames, 4096]
@group(0) @binding(4) var<uniform> p: P;
@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) id: vec3u) {
  let n2 = id.x;
  let n1 = id.y;
  let s = id.z;
  if (n2 >= 64u || n1 >= 64u || s >= 2u * p.frames) { return; }
  var acc = 0.0;
  for (var k1 = 0u; k1 < 64u; k1++) {
    let v = y[(s * 64u + k1) * 64u + n2];
    let t = tw[((k1 * n1) % 64u) * 64u];
    acc += v.x * t.x - v.y * t.y;
  }
  let n = n1 * 64u + n2;
  out[s * 4096u + n] = acc / 4096.0 * win[n];
}
`,_e=`
struct P { frames: u32, len: u32, source: u32, out_at: u32 }
@group(0) @binding(0) var<storage, read> fr: array<f32>;
@group(0) @binding(1) var<storage, read> env: array<f32>;
@group(0) @binding(2) var<storage, read> xt: array<f32>;      // [8, len]
@group(0) @binding(3) var<storage, read> tstats: array<f32>;
@group(0) @binding(4) var<storage, read_write> out: array<f32>; // [2, len] at out_at (one per source asked for)
@group(0) @binding(5) var<uniform> p: P;
@compute @workgroup_size(256)
fn main(@builtin(global_invocation_id) id: vec3u, @builtin(num_workgroups) groups: vec3u) {
  let i = id.x + id.y * groups.x * 256u;
  if (i >= 2u * p.len) { return; }
  let ch = i / p.len;
  let j = i - ch * p.len;
  let pos = 3584u + j;
  var acc = 0.0;
  let last = pos / 1024u;
  for (var jj = select(0u, last - 3u, last >= 3u); jj <= last; jj++) {
    if (jj < 2u || jj >= p.frames + 2u) { continue; }
    let n = pos - jj * 1024u;
    if (n < 4096u) { acc += fr[(ch * p.frames + jj - 2u) * 4096u + n]; }
  }
  let t = xt[(p.source * 2u + ch) * p.len + j] * tstats[2] + tstats[0];
  out[p.out_at + i] = acc / env[1536u + j] + t;
}
`,ve=172032,ye=[`drums`,`bass`,`other`,`vocals`],V=2048,be=e=>e.reduce((e,t)=>e*t,1),H=/* @__PURE__ */ new WeakMap;var xe=class e{constructor(e,t,n,r,i,a){this.device=e,this.steps=t,this.mixBuffer=n,this.outBuffer=r,this.owned=i,this.reported=!1,this.dispatches=t.filter(e=>e.kind===`dispatch`).length;let o=H.get(e);o||H.set(e,o=/* @__PURE__ */ new Map);let s=o.get(a);s||o.set(a,s=new y(this.dispatches)),this.costs=s}get bytes(){return this.owned.reduce((e,t)=>e+t.size,0)}static async create(t,n,r,i={}){if(r.segment!==172032)throw Error(`htdemucs weights are for a ${r.segment}-sample segment, not ${ve}`);if(i.f16&&!t.features.has(`shader-f16`))throw Error(`htdemucs f16 needs a device opened with shader-f16`);let a=i.sources??[i.source??0];if(a.length===0||a.some(e=>!(e>=0&&e<ye.length)))throw Error(`htdemucs sources must be indices into ${ye.join(`, `)}, got ${a.join(`, `)}`);let o=i.f16?`f16`:`f32`,s=le(o),c=de(o);t.pushErrorScope(`validation`);let l=ve,u=[],d=e=>{let n=t.createBuffer({size:Math.max(16,Math.ceil(e)*4),usage:GPUBufferUsage.STORAGE|GPUBufferUsage.COPY_SRC|GPUBufferUsage.COPY_DST});return u.push(n),n},f=d(Math.ceil(n.byteLength/4));t.queue.writeBuffer(f,0,n.buffer,n.byteOffset,n.byteLength);let p=e=>{let t=r.tensors[e];if(!t)throw Error(`htdemucs weights: missing tensor ${e}`);return t},m=e=>{let{byte_offset:t,shape:n,dtype:r}=p(e),i=be(n)*(r===`f16`?2:4);return{buffer:f,offset:t,size:Math.ceil(i/4)*4}},h=e=>p(e).dtype===`f16`,g=e=>p(e).shape[0],_=new Float32Array(fe*2);for(let e=0;e<fe;e+=1)_[e*2]=Math.cos(2*Math.PI*e/fe),_[e*2+1]=Math.sin(2*Math.PI*e/fe);let v=d(_.length);t.queue.writeBuffer(v,0,_);let y=V/4*48*168,b=d(2*l),x=d(2*l*a.length),ee=d(4),S=d(4),C=d(4*V),w=d(4096),T=d(64),E=d(2752512),D=d(4*V*168),O=d(y),k=d(y/8),A=d(2*y),j=[d(16*V*168),d(16*V*168)],M=d(16*V*168),N=d(8*l),P=d(1376256),F=[],I=[];for(let e=0;e<4;e+=1)F.push(d(48*2**e*(V/4**(e+1))*168)),I.push(d(48*2**e*(l/4**(e+1))));let L=1344,R=d(512*L),H=d(344064),xe=d(512*L),U=d(344064),W=d(512*L),Se=d(344064),Ce=d(1536*L),we=d(1032192),Te=d(688128),G=d(1024*L),Ee=d(512*L),De=d(344064),Oe=d(2048*L),ke=d(4*L*L),Ae=/* @__PURE__ */ new Map,je=e=>{let n=Ae.get(e);return n||(n=t.createComputePipeline({layout:`auto`,compute:{module:t.createShaderModule({code:e}),entryPoint:`main`}}),Ae.set(e,n)),n},Me=e=>{let n=new Uint32Array(Math.ceil(e.length/4)*4);n.set(e);let r=t.createBuffer({size:n.byteLength,usage:GPUBufferUsage.UNIFORM|GPUBufferUsage.COPY_DST});return t.queue.writeBuffer(r,0,n),u.push(r),r},Ne=[],K=`input`,Pe=/* @__PURE__ */ new Map([[oe,`add`],[c,`attn_out`],[s,`attn_scores`],[ue,`softmax`],[ae,`glu`],[ie,`gn_glu`],[re,`gn`],[he,`istft_a`],[ge,`istft_b`],[ce,`layer_norm`],[se,`normalize`],[_e,`overlap_add`],[ne,`stats_final`],[B,`stats`],[pe,`stft_a`],[me,`stft_b`]]),q=(e,n,r,i,a=``)=>{let o=je(e),s=[...n,Me(r)].map((e,t)=>({binding:t,resource:e instanceof GPUBuffer?{buffer:e}:e})),c=`${K} ${Pe.get(e)??`gemm`}${a?` ${a}`:``}`;Ne.push({kind:`dispatch`,label:c,pipeline:o,group:t.createBindGroup({layout:o.getBindGroupLayout(0),entries:s}),count:i})},J=e=>{let t=Math.ceil(e/256),n=Math.min(t,32768);return[n,Math.ceil(t/n),1]},Y=(e,t,n)=>Ne.push({kind:`stage`,name:e,buffer:t,floats:n}),Fe=(e,t,n,r,i,a,o,s)=>{let c=r*i,l=Math.max(1,Math.min(256,Math.floor(4096/n),Math.ceil(c/16384)));q(B,[e,t,w],[l,r,i,a,o,0],[l,n,1]),q(ne,[w,t],[l,c,0],[n,1,1]),q(B,[e,t,w],[l,r,i,a,o,1],[l,n,1]),q(ne,[w,t],[l,c,s],[n,1,1])},Ie=(e,t,n,r,i)=>{i?Fe(e,C,n,t,r,n*r,r,1):Fe(e,C,1,1,t*n*r,0,0,1)},X=(e,t,n,r,i=0,a=T)=>{let{sh:s=1,sw:c=1,ph:l=0,pw:u=0,dh:d=1,dw:f=1}=r,_=g(`${t}.w`),v=p(`${t}.w`).shape[1],y=v/(r.kh*r.kw),b=Math.floor((r.hin+2*l-d*(r.kh-1)-1)/s)+1,x=Math.floor((r.win+2*u-f*(r.kw-1)-1)/c)+1;return q(te(h(`${t}.w`),o),[e,m(`${t}.w`),m(`${t}.b`),a,n],[_,v,y,r.kh,r.kw,s,c,l,u,d,f,r.hin,r.win,b,x,i],[Math.ceil(b*x/64),Math.ceil(_/64),1],t.split(`.`).slice(1).join(`.`)),[b,x]},Le=(e,t,n,r,i,a,s)=>{let c=g(`${t}.w`),l=p(`${t}.w`).shape[1],u=a===0?(r+1)*i:r*(i+1);q(z(h(`${t}.w`),o),[e,m(`${t}.w`),m(`${t}.b`),n],[c,l,l/2,r,i,a,s],[Math.ceil(u/64),Math.ceil(c/64),1],`tr`)},Re=(e,t,n,r,i)=>{for(let a=0;a<2;a+=1){let o=`${e}.dc${a}`,s=2**a,c=g(`${o}.c1.w`);X(O,`${o}.c1`,k,{hin:n,win:r,kh:1,kw:3,pw:s,dw:s}),Ie(k,c,n,r,i),q(re,[k,C,m(`${o}.n1.g`),m(`${o}.n1.b`)],[c,n,r,+!!i,1],J(c*n*r)),X(k,`${o}.c2`,A,{hin:n,win:r,kh:1,kw:1}),Ie(A,2*t,n,r,i),q(ie,[A,C,m(`${o}.n2.g`),m(`${o}.n2.b`),m(`${o}.ls`),O],[t,n,r,+!!i],J(t*n*r))}},ze=N;Fe(b,ee,1,1,2*l,0,0,2),Ne.push({kind:`copy`,from:b,to:ze,bytes:2*l*4}),q(se,[ze,ee],[2*l],J(2*l)),q(pe,[b,m(`stft.window`),v,E],[168,l],[1,64,336]),q(me,[E,v,D],[168],[1,64,336]),Fe(D,S,1,1,4*V*168,0,0,2),q(se,[D,S],[4*V*168],J(4*V*168)),Y(`norm_spec`,D,4*V*168);let Be=ze,Ve=D,Z=l,Q=V;for(let e=0;e<4;e+=1){let t=48*2**e;K=`tenc${e}`;let[,n]=X(Be,`tenc${e}.conv`,O,{hin:1,win:Z,kh:1,kw:8,sw:4,pw:2},1);Z=n,Re(`tenc${e}`,t,1,Z,!1),X(O,`tenc${e}.rw`,A,{hin:1,win:Z,kh:1,kw:1}),q(ae,[A,T,I[e]],[t,1,Z,0],J(t*Z)),Y(`tenc${e}`,I[e],t*Z),Be=I[e],K=`enc${e}`;let[r]=X(Ve,`enc${e}.conv`,O,{hin:Q,win:168,kh:8,kw:1,sh:4,ph:2},1);Q=r,Re(`enc${e}`,t,Q,168,!0),X(O,`enc${e}.rw`,A,{hin:Q,win:168,kh:1,kw:1}),q(ae,[A,e===0?m(`freq_emb`):T,F[e]],[t,Q,168,+(e===0)],J(t*Q*168)),Y(`enc${e}`,F[e],t*Q*168),Ve=F[e]}K=`tf_in`,X(F[3],`up`,xe,{hin:1,win:L,kh:1,kw:1}),X(I[3],`up_t`,U,{hin:1,win:672,kh:1,kw:1}),q(ce,[xe,m(`norm_in.g`),m(`norm_in.b`),m(`pos`),R],[512,L,1],[21,1,1]),q(ce,[U,m(`norm_in_t.g`),m(`norm_in_t.b`),m(`pos_t`),H],[512,672,1],[11,1,1]),Y(`tf_in`,R,512*L),Y(`tf_in_t`,H,344064);let He=(e,t,n,r)=>q(ce,[e,m(`${t}.g`),m(`${t}.b`),T,n],[512,r,0],[Math.ceil(r/64),1,1]),$=(e,t,n,r,i=0,a=T)=>X(e,t,n,{hin:1,win:r,kh:1,kw:1},i,a),Ue=(e,t,n,r,i,a,o,l)=>{for(let u=0;u<8;u+=4){let d=`heads ${u}-${u+4-1}`;q(s,[e,t,ke],[r,i,a,o,u],[Math.ceil(i/64),Math.ceil(r/64),4],d);let f=4*r,p=Math.min(f,32768);q(ue,[ke],[i],[p,Math.ceil(f/p),1],d),q(c,[ke,t,n],[r,i,l,u],[1,Math.ceil(r/64),4],d)}},We=(e,t,n,r)=>{He(e,`${t}.nf`,r,n),$(r,`${t}.ff1`,Oe,n,1),$(Oe,`${t}.ff2`,e,n,2,m(`${t}.g2`)),Fe(e,C,1,1,512*n,0,0,1),q(re,[e,C,m(`${t}.no.g`),m(`${t}.no.b`)],[512,1,n,0,0],J(512*n))};for(let e=0;e<5;e+=1){let t=`tf${e}`,n=`tft${e}`;if(K=t,e%2==0)for(let[e,r,i,a,o,s]of[[R,t,L,xe,Ce,Ee],[H,n,672,U,we,De]])He(e,`${r}.n1`,a,i),$(a,`${r}.qkv`,o,i),Ue(o,o,s,i,i,0,512,1024),$(s,`${r}.out`,e,i,2,m(`${r}.g1`)),We(e,r,i,a);else He(R,`${t}.n1`,xe,L),$(xe,`${t}.q`,Ce,L),He(H,`${t}.nkv`,Se,672),$(Se,`${t}.kv`,Te,672),He(H,`${n}.n1`,U,672),$(U,`${n}.q`,we,672),He(R,`${n}.nkv`,W,L),$(W,`${n}.kv`,G,L),Ue(Ce,Te,Ee,L,672,0,0,512),Ue(we,G,De,672,L,0,0,512),$(Ee,`${t}.out`,R,L,2,m(`${t}.g1`)),$(De,`${n}.out`,H,672,2,m(`${n}.g1`)),We(R,t,L,xe),We(H,n,672,U);Y(`tf${e}`,R,512*L),Y(`tft${e}`,H,344064)}let Ge=j[0];K=`dec0`,$(R,`down`,Ge,L);for(let e=0;e<4;e+=1){let t=48*2**(3-e);K=`dec${e}`;let n=Q*168;q(oe,[Ge,F[3-e]],[t*n],J(t*n)),X(Ge,`dec${e}.rw`,A,{hin:Q,win:168,kh:3,kw:3,ph:1,pw:1}),q(ae,[A,T,O],[t,Q,168,0],J(t*n)),Re(`dec${e}`,t,Q,168,!0);let r=e===3?M:j[(e+1)%2];Le(O,`dec${e}.tr`,r,Q,168,0,e===3?0:1),Q*=4,Y(`dec${e}`,r,g(`dec${e}.tr.w`)/4*Q*168),Ge=r}Ge=j[0],K=`tdec0`,$(H,`down_t`,Ge,672);for(let e=0;e<4;e+=1){let t=48*2**(3-e);K=`tdec${e}`,q(oe,[Ge,I[3-e]],[t*Z],J(t*Z)),X(Ge,`tdec${e}.rw`,A,{hin:1,win:Z,kh:1,kw:3,pw:1}),q(ae,[A,T,O],[t,1,Z,0],J(t*Z)),Re(`tdec${e}`,t,1,Z,!1);let n=e===3?N:j[(e+1)%2];Le(O,`tdec${e}.tr`,n,1,Z,1,e===3?0:1),Z*=4,Y(`tdec${e}`,n,g(`tdec${e}.tr.w`)/4*Z),Ge=n}K=`output`,a.forEach((e,t)=>{q(he,[M,S,v,E],[168,e],[1,64,336],ye[e]),q(ge,[E,v,m(`istft.window`),P],[168],[1,64,336],ye[e]),q(_e,[P,m(`istft.env`),N,ee,x],[168,l,e,t*2*l],J(2*l),ye[e]),t<a.length-1&&Y(`out_${ye[e]}`,x,2*l*(t+1))}),Y(`out`,x,2*l*a.length);let Ke=await t.popErrorScope();if(Ke)throw u.forEach(e=>e.destroy()),Error(`htdemucs graph rejected: ${Ke.message}`);let qe=new e(t,Ne,b,x,u,`${a.join(`,`)}|${o}`);return await t.queue.onSubmittedWorkDone(),qe}async run(e,t,n={}){let r=ve;if(e.length!==r||t.length!==r)throw Error(`htdemucs segment must be ${r} samples per channel, got ${e.length} and ${t.length}`);let{device:i}=this;i.queue.writeBuffer(this.mixBuffer,0,e.buffer,e.byteOffset,e.byteLength),i.queue.writeBuffer(this.mixBuffer,r*4,t.buffer,t.byteOffset,t.byteLength);let a=n.slice??`none`,o=a===`paced`,s=o?n.pacing??g:h,c=!1;o&&this.costs.wanted&&(!i.features.has(`timestamp-query`)||this.dispatches*2>4096)&&(this.costs.giveUp(),c=!0);let l=o&&this.costs.wanted?i.createQuerySet({type:`timestamp`,count:this.dispatches*2}):null,u=o?this.costs.cutsFor(s.sliceMs):null,d=_(i.queue,o&&!u?{...s,inFlight:1}:s),f=i.createCommandEncoder(),p=null,m=!1,v=[],y=async(e=!1)=>{p?.end(),p=null;let t=f.finish();f=i.createCommandEncoder(),m=!1;let n=performance.now();return await d.submit(t,e),performance.now()-n},S=[],C=``,w=0,T=performance.now();for(let e of this.steps){if(a===`dispatch`&&p){let e=await y();n.timings&&S.push({label:C,ms:e})}if(e.kind===`dispatch`){u?.has(w)&&m&&await y(),C=e.label,l&&(p?.end(),p=f.beginComputePass({timestampWrites:{querySet:l,beginningOfPassWriteIndex:w*2,endOfPassWriteIndex:w*2+1}})),p??=f.beginComputePass(),p.setPipeline(e.pipeline),p.setBindGroup(0,e.group),p.dispatchWorkgroups(...e.count),w+=1,m=!0;continue}if(e.kind===`copy`){p?.end(),p=null,f.copyBufferToBuffer(e.from,0,e.to,0,e.bytes),m=!0;continue}if((a===`stage`||o&&!u)&&e.name!==`out`&&m&&await y(),!n.stages&&e.name!==`out`)continue;p?.end(),p=null;let t=i.createBuffer({size:e.floats*4,usage:GPUBufferUsage.MAP_READ|GPUBufferUsage.COPY_DST});f.copyBufferToBuffer(e.buffer,0,t,0,e.floats*4),v.push({name:e.name,buffer:t})}let E=null;if(l){p?.end(),p=null;let e=this.dispatches*16,t=i.createBuffer({size:e,usage:GPUBufferUsage.QUERY_RESOLVE|GPUBufferUsage.COPY_SRC});E=i.createBuffer({size:e,usage:GPUBufferUsage.MAP_READ|GPUBufferUsage.COPY_DST}),f.resolveQuerySet(l,0,this.dispatches*2,t,0),f.copyBufferToBuffer(t,0,E,0,e),await y(!0),t.destroy(),l.destroy()}else await y(!0);let D=performance.now()-T,O;E&&(await E.mapAsync(GPUMapMode.READ),this.costs.record(b(new BigInt64Array(E.getMappedRange()),this.dispatches)),E.destroy());let k=o?this.costs.costs:null;k&&!this.reported&&(O=ee(k,s.sliceMs),this.reported=!0),o&&this.costs.unusable&&(c=!0);let A={};for(let{name:e,buffer:t}of v)await t.mapAsync(GPUMapMode.READ),A[e]=new Float32Array(t.getMappedRange().slice(0)),t.destroy();let j=A.out;n.stages||delete A.out;let M=Array.from({length:j.length/(2*r)},(e,t)=>({left:j.subarray(t*2*r,t*2*r+r),right:j.subarray(t*2*r+r,(t+1)*2*r)}));return{left:M[0].left,right:M[0].right,stems:M,gpuMs:d.waitedMs,wallMs:D,slices:d.slices,...o?{paced:{slices:d.slices,sliceMs:s.sliceMs,byCosts:u!==null,inFlight:u?s.inFlight:1,pacedInFlight:s.inFlight,roundTripMs:x(d.roundTrips),...O?{costs:O}:{},...c?{noTimestamps:c}:{}}}:{},...n.stages?{stages:A}:{},...n.timings?{timings:S}:{}}}destroy(){this.owned.forEach(e=>e.destroy())}};const U=/* @__PURE__ */ new Map;let W=null,Se=null,Ce=``,we=``,Te=!1,G=null;const Ee=S(typeof self.requestAnimationFrame==`function`?e=>self.requestAnimationFrame(e):void 0),De=/Mac/.test(navigator.platform)?1:4,Oe=()=>({sliceMs:C(Ee.frameMs),inFlight:De,frame:Ee.next}),ke=async()=>{if(Se)return Se;if(!navigator.gpu)throw Error(`no WebGPU in this worker`);let e=await navigator.gpu.requestAdapter({powerPreference:`high-performance`});if(!e)throw Error(`no WebGPU adapter`);Te=e.features.has(`shader-f16`);let t=[];Te&&t.push(`shader-f16`),e.features.has(`timestamp-query`)&&t.push(`timestamp-query`);let n=await e.requestDevice({requiredFeatures:t});return n.lost.then(e=>{G=`device lost (${e.reason}): ${e.message}`,U.clear(),W=null}),we=`${e.info.vendor} ${e.info.architecture}`,Ce=`${we}${Te?` f16`:``}`,Se=n,n};self.onmessage=async e=>{let t=e.data;try{if(t.kind===`load`){let e;try{e=await ke()}catch(e){self.postMessage({id:t.id,error:String(e?.message??e),scope:`device`});return}if(t.model===`htdemucs`){if(!W)try{W=await xe.create(e,t.weights,t.manifest,{sources:[0,1,2,3]})}catch(e){self.postMessage({id:t.id,error:String(e?.message??e),scope:`model`});return}self.postMessage({id:t.id,result:`${we} f32`,bytes:W?.bytes});return}if(!U.has(t.model))try{U.set(t.model,await O.create(e,t.weights,t.manifest,{f16:Te?`storage`:void 0}))}catch(e){self.postMessage({id:t.id,error:String(e?.message??e),scope:`model`});return}self.postMessage({id:t.id,result:Ce,bytes:U.get(t.model)?.bytes});return}if(t.kind===`percussive`){let e;try{e=await ke()}catch(e){self.postMessage({id:t.id,error:String(e?.message??e),scope:`device`});return}if(G)throw Error(G);let n=await j(e,t.mel,t.frames,Oe());self.postMessage({id:t.id,result:n},{transfer:[n.percussive.buffer,n.total.buffer]});return}if(t.kind===`separate`){if(G)throw Error(G);if(!W)throw Error(`htdemucs not loaded`);let e=await W.run(t.left,t.right,{slice:`paced`,pacing:Oe()}),{stems:n}=e;self.postMessage({id:t.id,result:{stems:n,paced:e.paced}},{transfer:[n[0].left.buffer]});return}if(t.kind===`unload`){t.model===`htdemucs`?(W?.destroy(),W=null):(U.get(t.model)?.destroy(),U.delete(t.model)),self.postMessage({id:t.id,result:!0});return}if(t.kind===`dispose`){for(let e of U.values())e.destroy();U.clear(),W?.destroy(),W=null,Se?.destroy(),self.postMessage({id:t.id,result:!0});return}let e=U.get(t.model),n=[],r=[],i;for(let a of t.chunks){if(G)throw Error(G);if(!e)throw Error(`${t.model} not loaded`);let o=await e.run(a,{slice:`paced`,pacing:Oe()});(!i||o.paced?.costs)&&(i=o.paced),n.push(o.beat.slice()),r.push(o.downbeat.slice())}let a;if(t.percussive&&Se&&!G)try{a=await j(Se,t.percussive.mel,t.percussive.frames,Oe())}catch(e){console.warn(`[beat-this-gpu] percussive energy failed: ${e?.message??e}`)}let o=[...n,...r,...a?[a.percussive,a.total]:[]];self.postMessage({id:t.id,result:{beat:n,downbeat:r,percussive:a,paced:i}},{transfer:o.map(e=>e.buffer)})}catch(e){self.postMessage({id:t.id,error:String(e?.message??e)})}};