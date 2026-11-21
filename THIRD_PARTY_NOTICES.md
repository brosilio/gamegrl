# Third-party notices

gamegrl is released under the BSD Zero Clause License (see `LICENSE`). The code listed here is ported from other
projects and stays under their licenses: if you copy it, keep its notice with it.

## Box2D (MIT)

Physics 2D follows Box2D v3's design, and parts of it are ported from Box2D's source (commit `2b14ae3`): the broadphase
and dynamic tree, the solver, joints, islands, collision and manifolds, distance and continuous collision, the convex
hull. The benchmark scenes in `tests/Gamegrl.PhysicsBench` are ported from Box2D's `benchmark/`.

Files: everything in `src/Gamegrl.Physics2D/` and `tests/Gamegrl.PhysicsBench/`. Each source file there carries a
header pointing here.

```
MIT License

Copyright (c) 2022 Erin Catto

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Jolt Physics (MIT)

Physics 3D measures itself against Jolt Physics (commit `264f6d06`, v5.7.0+14): its features, and the scenes of its
`PerformanceTest`, which will be ported to `tests/Gamegrl.PhysicsBench3D`. Code ported from Jolt goes in
`src/Gamegrl.Physics3D/` and `tests/Gamegrl.PhysicsBench3D/`, each file carrying a header pointing here.

```
Copyright 2021 Jorrit Rouwe

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## AnsiDotNet (0BSD)

`external/ansidotnet` is a git submodule (the editor's terminal UI uses `AnsiDotNet.Layout`), under its own BSD Zero
Clause License.

## Libraries loaded at run time (not distributed here)

Loaded from the system by name, never bundled: SDL3 (zlib license), the Vulkan loader (Apache 2.0), ALSA's
`libasound` (LGPL 2.1, loaded dynamically), OpenSSL (Apache 2.0, for QUIC), the Khronos OpenXR loader (Apache 2.0,
XR module only). The desktop editor uses the system's DejaVu Sans Mono font (or `GAMEGRL_FONT`). The struct sizes in
`VkLayoutTests` are checked against the system's `vulkan_core.h`, which isn't copied here.
