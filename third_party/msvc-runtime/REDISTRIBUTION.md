# Microsoft Visual C++ runtime redistribution

The adjacent DLLs are unmodified app-local copies from the Visual Studio 2019
Build Tools `Microsoft.VC142.CRT` redistributable directory. Microsoft permits
redistribution of these runtime files with applications under the Visual Studio
license. They are included so the portable WinUI release does not require a
separate VC++ Redistributable installation.

Source installation version: Microsoft Visual C++ Redistributable 14.51.36247,
x64. The runtime must be at least as new as the ONNX Runtime build; older
VC142 app-local DLLs fail during `onnxruntime.dll` initialization.
