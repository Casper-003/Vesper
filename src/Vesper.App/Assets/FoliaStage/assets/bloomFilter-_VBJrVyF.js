import{t as e}from"./pixiFilterVertex-DMAitlUE.js";var t=globalThis.devicePixelRatio|0,n=(102^t)+Math.imul(111^t,108^t)-((102^t)+Math.imul(111^t,108^t)),r=`
in vec2 vTextureCoord;
out vec4 finalColor;
uniform sampler2D uTexture;
uniform vec4 uInputPixel;
uniform vec4 uInputClamp;
uniform vec4 uParams; // threshold, knee, prefilter(0/1), 0

vec4 tap(vec2 uv) {
    return texture(uTexture, clamp(uv, uInputClamp.xy, uInputClamp.zw));
}

void main(void) {
    vec2 uv = vTextureCoord;
    vec2 hp = uInputPixel.zw;
    vec4 sum = tap(uv) * 4.0;
    sum += tap(uv - hp);
    sum += tap(uv + hp);
    sum += tap(uv + vec2(hp.x, -hp.y));
    sum += tap(uv - vec2(hp.x, -hp.y));
    vec4 c = sum / 8.0;
    if (uParams.z > 0.5) {
        float br = max(c.r, max(c.g, c.b));
        float knee = max(uParams.y, 1e-4);
        float soft = clamp(br - uParams.x + knee, 0.0, 2.0 * knee);
        soft = soft * soft / (4.0 * knee);
        float contrib = max(soft, br - uParams.x) / max(br, 1e-4);
        c *= clamp(contrib, 0.0, 1.0);
    }
    finalColor = c;
}
`,i=`
in vec2 vTextureCoord;
out vec4 finalColor;
uniform sampler2D uTexture;
uniform sampler2D uAdd;
uniform vec4 uInputPixel;
uniform vec4 uInputClamp;
uniform vec4 uAddMap;   // uv 缩放 xy、同级权重、0
uniform vec4 uAddClamp;

vec4 tap(vec2 uv) {
    return texture(uTexture, clamp(uv, uInputClamp.xy, uInputClamp.zw));
}

void main(void) {
    vec2 uv = vTextureCoord;
    vec2 hp = uInputPixel.zw;
    vec4 sum = tap(uv + vec2(-hp.x * 2.0, 0.0));
    sum += tap(uv + vec2(-hp.x, hp.y)) * 2.0;
    sum += tap(uv + vec2(0.0, hp.y * 2.0));
    sum += tap(uv + vec2(hp.x, hp.y)) * 2.0;
    sum += tap(uv + vec2(hp.x * 2.0, 0.0));
    sum += tap(uv + vec2(hp.x, -hp.y)) * 2.0;
    sum += tap(uv + vec2(0.0, -hp.y * 2.0));
    sum += tap(uv + vec2(-hp.x, -hp.y)) * 2.0;
    vec4 up = sum / 12.0;
    vec4 same = texture(uAdd, clamp(uv * uAddMap.xy, uAddClamp.xy, uAddClamp.zw));
    finalColor = up + same * uAddMap.z;
}
`,a=`
in vec2 vTextureCoord;
out vec4 finalColor;
uniform sampler2D uTexture;
uniform sampler2D uAdd;
uniform vec4 uInputClamp;
uniform vec4 uAddMap;   // uv 缩放 xy、强度、0
uniform vec4 uAddClamp;
uniform vec4 uTint;     // 乘在 bloom 上的颜色

void main(void) {
    vec4 base = texture(uTexture, clamp(vTextureCoord, uInputClamp.xy, uInputClamp.zw));
    vec4 glow = texture(uAdd, clamp(vTextureCoord * uAddMap.xy, uAddClamp.xy, uAddClamp.zw)) * uAddMap.z;
    vec3 g = glow.rgb * uTint.rgb;
    // 加上去的辉光也是光：预乘、alpha 取最大通道（与光场一致，近似 screen）。
    vec3 rgb = base.rgb + g * (1.0 - base.a * 0.5);
    // 超过 1 时按最大通道整体缩回（保持金色），只让一小部分变白；逐通道截断会把亮字烧成白块。
    float peak = max(max(rgb.r, rgb.g), rgb.b);
    if (peak > 1.0) rgb = mix(rgb / peak, vec3(1.0), 0.2 * (1.0 - 1.0 / peak));
    float a = base.a + max(max(g.r, g.g), g.b) * (1.0 - base.a);
    finalColor = vec4(rgb, min(max(a, max(max(rgb.r, rgb.g), rgb.b)), 1.0));
}
`,o=(t,o)=>{let{Filter:s,GlProgram:c,TexturePool:l,UniformGroup:u}=t,d=(t,n)=>c.from({vertex:e,fragment:t,name:n}),f=new u({uParams:{value:new Float32Array(4),type:`vec4<f32>`}}),p=new s({glProgram:d(r,`lumiere-bloom-down`),resources:{bloomDown:f}}),m=new u({uAddMap:{value:new Float32Array(4),type:`vec4<f32>`},uAddClamp:{value:new Float32Array(4),type:`vec4<f32>`}}),h=new s({glProgram:d(i,`lumiere-bloom-up`),resources:{bloomUp:m,uAdd:t.Texture.EMPTY.source}}),g=new u({uAddMap:{value:new Float32Array(4),type:`vec4<f32>`},uAddClamp:{value:new Float32Array(4),type:`vec4<f32>`},uTint:{value:new Float32Array([1,1,1,1]),type:`vec4<f32>`}}),_=new s({glProgram:d(a,`lumiere-bloom-combine`),resources:{bloomCombine:g,uAdd:t.Texture.EMPTY.source}}),v=(e,t,n,r,i)=>{n[0]=e.source.width/t.source.width,n[1]=e.source.height/t.source.height,n[2]=i,r[0]=.5/t.source.pixelWidth,r[1]=.5/t.source.pixelHeight,r[2]=t.frame.width/t.source.width-.5/t.source.pixelWidth,r[3]=t.frame.height/t.source.height-.5/t.source.pixelHeight};class y extends s{constructor(e){super({resources:{},compatibleRenderers:t.RendererType.BOTH}),this.passes=[p,h,_],this.options={...e},this.padding=e.padding}apply(e,t,r,i){let{strength:a,threshold:o,knee:s,levels:c,spread:u,tint:d}=this.options,y=()=>{v(t,t,g.uniforms.uAddMap,g.uniforms.uAddClamp,0),g.update(),_.resources.uAdd=t.source,_.blendMode=this.blendMode,e.applyFilter(_,t,r,i)};if(a<=0){y();return}let b=t.frame.width,x=t.frame.height,S=t.source.resolution,C=[],w=t,T=Math.max(1,Math.min(8+n,Math.round(c)));for(let t=1;t<=T;t+=1){let n=S/2**t;if(Math.min(b,x)*n<2)break;let r=l.getOptimalTexture({width:b,height:x,resolution:n}),i=f.uniforms.uParams;i[0]=o,i[1]=s,i[2]=+(t===1),f.update(),p.blendMode=`normal`,e.applyFilter(p,w,r,!0),C.push(r),w=r}if(C.length===0){y();return}let E=C[C.length-1],D=[];for(let t=C.length-2;t>=0;--t){let n=C[t],r=l.getOptimalTexture({width:b,height:x,resolution:n.source.resolution});v(E,n,m.uniforms.uAddMap,m.uniforms.uAddClamp,u),m.update(),h.resources.uAdd=n.source,h.blendMode=`normal`,e.applyFilter(h,E,r,!0),D.push(r),E=r}v(t,E,g.uniforms.uAddMap,g.uniforms.uAddClamp,a);let O=g.uniforms.uTint;O[0]=d[0],O[1]=d[1],O[2]=d[2],g.update(),_.resources.uAdd=E.source,_.blendMode=this.blendMode,e.applyFilter(_,t,r,i),C.forEach(e=>l.returnTexture(e)),D.forEach(e=>l.returnTexture(e))}destroy(){p.destroy(),h.destroy(),_.destroy(),super.destroy()}}return new y(o)};export{o as t};