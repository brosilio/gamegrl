#version 450
layout(set = 0, binding = 0) uniform Camera { mat4 viewProj; vec4 lightDir; vec4 ambient; } cam;

layout(location = 0) in vec3 vNormal;
layout(location = 1) in vec4 vColor;
layout(location = 0) out vec4 outColor;

void main() {
    float diffuse = max(dot(normalize(vNormal), -cam.lightDir.xyz), 0.0);
    outColor = vec4(vColor.rgb * (cam.ambient.rgb + diffuse), vColor.a);
}
