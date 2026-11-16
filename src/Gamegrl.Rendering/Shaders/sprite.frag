#version 450
layout(set = 1, binding = 0) uniform texture2D spriteTexture;
layout(set = 1, binding = 1) uniform sampler spriteSampler;

layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 0) out vec4 outColor;

void main() {
    outColor = texture(sampler2D(spriteTexture, spriteSampler), vUv) * vColor;
}
