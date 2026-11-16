#version 450
// UI vertices are in pixels, origin top-left. Colors are authored in sRGB and converted to linear here, because
// the swapchain is sRGB (the hardware converts back on write).
layout(set = 0, binding = 0) uniform Screen { vec4 size; } screen; // width, height, 1/width, 1/height

layout(location = 0) in vec2 inPos;
layout(location = 1) in vec2 inUv;
layout(location = 2) in vec4 inColor;

layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;

vec3 toLinear(vec3 c) {
    return mix(c / 12.92, pow((c + 0.055) / 1.055, vec3(2.4)), step(vec3(0.04045), c));
}

void main() {
    vec2 ndc = vec2(inPos.x * screen.size.z * 2.0 - 1.0, 1.0 - inPos.y * screen.size.w * 2.0);
    gl_Position = vec4(ndc, 0.0, 1.0);
    vUv = inUv;
    vColor = vec4(toLinear(inColor.rgb), inColor.a);
}
