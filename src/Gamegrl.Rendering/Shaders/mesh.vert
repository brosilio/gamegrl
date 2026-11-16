#version 450
// Matrices are uploaded as System.Numerics lays them out (row-major, row vectors), which GLSL reads as the
// transpose; so `M * v` here equals `v * M` in C#.
layout(set = 0, binding = 0) uniform Camera { mat4 viewProj; vec4 lightDir; vec4 ambient; } cam;

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec3 inNormal;
layout(location = 7) in vec2 inUv; // unused until materials (milestone 3+)
layout(location = 2) in vec4 m0;
layout(location = 3) in vec4 m1;
layout(location = 4) in vec4 m2;
layout(location = 5) in vec4 m3;
layout(location = 6) in vec4 inColor;

layout(location = 0) out vec3 vNormal;
layout(location = 1) out vec4 vColor;

void main() {
    mat4 model = mat4(m0, m1, m2, m3);
    gl_Position = cam.viewProj * (model * vec4(inPos, 1.0));
    vNormal = mat3(model) * inNormal;
    vColor = inColor;
}
