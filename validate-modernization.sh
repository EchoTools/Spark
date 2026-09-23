#!/bin/bash
set -e

echo "Validating .NET 10.0 Modernization..."

# Check global.json
if grep -q '"version": "10.0.0"' global.json; then
    echo "✅ global.json is using .NET 10.0"
else
    echo "❌ global.json is not using .NET 10.0"
    exit 1
fi

# Check Spark.csproj framework
if grep -q '<TargetFramework>net10.0-windows10.0.17763.0</TargetFramework>' Spark.csproj || grep -q '<TargetFramework>net10.0-windows</TargetFramework>' Spark.csproj; then
    echo "✅ Spark.csproj targets net10.0-windows"
else
    echo "❌ Spark.csproj does not target net10.0-windows"
    exit 1
fi

# Check WindowsAPICodePack removal
if grep -q 'WindowsAPICodePack-Shell' Spark.csproj; then
    echo "❌ WindowsAPICodePack-Shell still exists in Spark.csproj"
    exit 1
else
    echo "✅ WindowsAPICodePack-Shell is removed from Spark.csproj"
fi

# Check Deprecated API usage
if grep -q 'CommonOpenFileDialog' Windows/Settings/UnifiedSettingsWindow.xaml.cs; then
    echo "❌ CommonOpenFileDialog still used in UnifiedSettingsWindow.xaml.cs"
    exit 1
else
    echo "✅ CommonOpenFileDialog is removed from UnifiedSettingsWindow.xaml.cs"
fi

if grep -q 'OpenFolderDialog' Windows/Settings/UnifiedSettingsWindow.xaml.cs; then
    echo "✅ OpenFolderDialog is used in UnifiedSettingsWindow.xaml.cs"
else
    echo "❌ OpenFolderDialog not found in UnifiedSettingsWindow.xaml.cs"
    exit 1
fi

echo "All checks passed successfully."
