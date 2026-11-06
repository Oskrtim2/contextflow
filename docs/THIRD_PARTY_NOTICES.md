# Third-Party Notices

ContextFlow incorporates the following third-party components. Review and include the complete upstream notices in your commercial distribution package.

## ONNX Runtime

- Package: `Microsoft.ML.OnnxRuntime` 1.22.1 and its managed/native runtime components.
- Copyright: © Microsoft Corporation.
- License: MIT License.
- Project: https://github.com/microsoft/onnxruntime
- Upstream third-party notice bundle: https://github.com/microsoft/onnxruntime/blob/main/ThirdPartyNotices.txt

The full ONNX Runtime license and third-party notice bundle are included in `docs/third-party/` when this repository is packaged. The native runtime may include additional components listed in the upstream `ThirdPartyNotices.txt`. If you modify or update ONNX Runtime, regenerate that notice bundle for the exact version you ship.

MIT License text (ONNX Runtime):

```text
MIT License

Copyright (c) Microsoft Corporation

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

## .NET / Windows Desktop Runtime

Self-contained distribution includes Microsoft .NET components subject to their respective licenses. A published self-contained application includes the .NET runtime; consult the .NET license and third-party notices for the runtime version used (`runtimeconfig.json`/`deps.json` in build output) before commercial distribution.

## Windows platform

Windows, Win32, DWM, and related platform APIs are part of Microsoft Windows and are not distributed with this source repository. This project does not ship Microsoft UI framework source or proprietary SDK binaries beyond those contained in the .NET/ONNX Runtime NuGet packages.

## Generated assets

The ContextFlow application icon (`Assets/AppIcon.ico`) and policy model (`Assets/policy.onnx`) are original generated assets produced by the scripts in `tools/`.
