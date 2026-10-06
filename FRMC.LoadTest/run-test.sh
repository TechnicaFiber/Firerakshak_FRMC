#!/bin/bash

echo "╔═══════════════════════════════════════════════════════════╗"
echo "║  FRMC Channel Switching Load Test                        ║"
echo "║  Make sure FRMC is running before starting this test!    ║"
echo "╚═══════════════════════════════════════════════════════════╝"
echo ""

# Check if FRMC is running
if pgrep -f "FireRakshak.FRMC.Host" > /dev/null; then
    echo "✅ FRMC is running - starting load test..."
    echo ""
else
    echo "❌ FRMC is NOT running!"
    echo ""
    echo "Please start FRMC first:"
    echo "  cd ../FireRakshak.FRMC.Host/bin/Release/net8.0"
    echo "  dotnet FireRakshak.FRMC.Host.dll"
    echo ""
    exit 1
fi

# Run the test
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
dotnet run --project "$SCRIPT_DIR/FRMC.LoadTest.csproj" --configuration Release

echo ""
echo "Test completed!"
