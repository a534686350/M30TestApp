# F40MultiCalibrator source

This directory is the buildable source snapshot for the F40 compensation, calibration and test workstation.

## Requirements

- Windows 10/11
- .NET 8 SDK
- x86 VISA runtime and `Ivi.Visa.Interop`
- `support/CalibrationL6.dll` kept beside the project output

The project targets `win-x86` because `CalibrationL6.dll` is 32-bit.

## Build

```powershell
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r win-x86 --self-contained true
```

## Version rollback

Use an F40-specific tag so rollback does not affect the M30 application in the same repository:

```powershell
git fetch --tags
git switch --detach f40-v1.0.1
cd f40/src/F40MultiCalibrator
dotnet publish -c Release -r win-x86 --self-contained true
```

Runtime device configuration lives in `setting`. Online updates intentionally do not overwrite `setting`, `logs` or `标定结果`.
