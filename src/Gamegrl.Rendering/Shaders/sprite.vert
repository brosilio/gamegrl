#version 450
// One instance per sprite; the quad's corners come from gl_VertexIndex (no vertex buffer for geometry).
layout(set = 0, binding = 0) uniform Screen { mat4 proj; } screen;

layout(location = 0) in vec4 inPosSize;   // center x, y; width, height (pixels)
layout(location = 1) in vec4 inUv;        // u0, v0, u1, v1
layout(location = 2) in vec4 inColor;
layout(location = 3) in vec4 inRotPivot;  // cos, sin, pivot x, pivot y (0..1)

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;

const vec2 corners[6] = vec2[](vec2(0, 0), vec2(1, 0), vec2(1, 1), vec2(0, 0), vec2(1, 1), vec2(0, 1));

void main() {
    vec2 c = corners[gl_VertexIndex];
    vec2 local = (c - inRotPivot.zw) * inPosSize.zw;
    vec2 rotated = vec2(local.x * inRotPivot.x - local.y * inRotPivot.y, local.x * inRotPivot.y + local.y * inRotPivot.x);
    gl_Position = screen.proj * vec4(inPosSize.xy + rotated, 0.0, 1.0);
    vUv = mix(inUv.xy, inUv.zw, vec2(c.x, 1.0 - c.y));
    vColor = inColor;
}
