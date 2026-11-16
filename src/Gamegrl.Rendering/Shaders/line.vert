#version 450
layout(set = 0, binding = 0) uniform Camera { mat4 viewProj; vec4 lightDir; vec4 ambient; } cam;

layout(location = 0) in vec3 inPos;
layout(location = 1) in vec4 inColor;
layout(location = 0) out vec4 vColor;

void main() {
    gl_Position = cam.viewProj * vec4(inPos, 1.0);
    vColor = inColor;
}
