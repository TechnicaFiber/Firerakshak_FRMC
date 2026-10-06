# FRMC Channel Switching Load Test

## Overview

Comprehensive load test that simulates:
- **Mock DTS Device**: Generates temperature data for 8 channels (10km each) every 5 seconds
- **Mock DTSCM Clients**: Two clients performing rapid channel switching and requesting health parameters
- **Validates**: No concurrent `SendAsync` crashes occur during channel switching

## Test Scenarios

### 1. Mock DTS Simulator
- Simulates 8 channels
- Each channel: 10km length (25,000 temperature points at 0.4m resolution)
- Scan cycle: Every 5 seconds
- Temperature: Channel N = N×100°C ± 5°C variation

### 2. Mock DTSCM Clients (2 clients)
Each client performs:
- **Login** to FRMC
- **Start live measurement**
- **Rapid channel switching**: Random channels every 2-5 seconds
- **GetHealthParameters** requests during switching (tests concurrent SendAsync)
- **GetZoneState** requests (tests broadcast concurrency)

## What This Tests

✅ **Channel switching under load** - Validates no connection loss
✅ **Concurrent SendAsync** - Ensures single lock prevents crashes
✅ **Response timeout prevention** - Validates 10s timeout is sufficient
✅ **Broadcast skipping** - Ensures broadcasts don't block responses
✅ **Multi-client scenario** - Tests with 2 concurrent DTSCM clients

## Expected Results

### ✅ SUCCESS Indicators:
```
- Test runs for full 5 minutes without errors
- Both clients complete all channel switches
- GetHealthParameters always responds (no timeouts)
- Live temperature data continues flowing
- May see "Broadcast skipped" in FRMC logs (normal)
```

### ❌ FAILURE Indicators:
```
- "CONNECTION LOST - Server closed connection prematurely"
- "GetHealthParameters failed" or timeout errors
- WebSocket crashes or exceptions
- FRMC logs show "InvalidOperationException: already one outstanding SendAsync"
```

## How to Run

### Prerequisites:
1. **FRMC must be running** with the new fixes
2. **DTS Simulator** (optional - this test has its own mock)

### Step 1: Build the test project
```bash
cd "c:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL"
dotnet build FRMC.LoadTest/FRMC.LoadTest.csproj
```

### Step 2: Start FRMC
```bash
cd "c:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL\FireRakshak.FRMC.Host\bin\Release\net8.0"
dotnet FireRakshak.FRMC.Host.dll
```

### Step 3: Run the load test (in separate terminal)
```bash
cd "c:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL"
dotnet run --project FRMC.LoadTest/FRMC.LoadTest.csproj
```

### Step 4: Monitor FRMC logs (in third terminal)
```bash
tail -f "c:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL\FireRakshak.FRMC.Host\bin\Release\net8.0\Logs\frmc-*.log"
```

## Test Configuration

Edit `Program.cs` to customize:

```csharp
const int NUM_CHANNELS = 8;                    // Number of channels to test
const double CHANNEL_LENGTH_KM = 10.0;         // Channel length in km
const int SCAN_INTERVAL_SECONDS = 5;           // DTS scan interval
const int TEST_DURATION_MINUTES = 5;           // Test duration
const string FRMC_WS_URL = "ws://localhost:6164/general_ws/?token=connecting";
```

## Interpreting Results

### Good Test Output:
```
✅ [Client-1] GetHealthParameters succeeded (no timeout!)
📊 [Client-1] Received live data: Channel 3, 25000 points
🔄 [Client-2] Switch #15: Channel 5 → 2
✅ Test completed successfully!
```

### Bad Test Output (indicates bug):
```
❌ [Client-1] CONNECTION LOST - Server closed connection prematurely!
   This indicates the concurrent SendAsync crash occurred!
❌ [Client-2] GetHealthParameters failed!
❌ Test failed with 2 errors
```

## Troubleshooting

**Issue:** Test fails to connect
**Solution:** Ensure FRMC is running on port 6164

**Issue:** "GetHealthParameters timeout"
**Solution:** Check if broadcasts are taking > 10 seconds (increase timeout or optimize broadcast size)

**Issue:** "CONNECTION LOST"
**Solution:** Check FRMC logs for `InvalidOperationException` - the fix may not be applied correctly

## Advanced Testing

### Stress Test (increase load):
```csharp
const int NUM_CHANNELS = 16;               // More channels
const int TEST_DURATION_MINUTES = 30;      // Longer test
// Add more DTSCM clients in Main()
var dtscmTask3 = Task.Run(() => RunMockDtscmClient("Client-3", ...));
```

### Network Latency Simulation:
Add delays in `SendMessage()`:
```csharp
await Task.Delay(50);  // Simulate 50ms network latency
await ws.SendAsync(...);
```

## Success Criteria

The test is considered **PASSED** if:
1. ✅ Runs for full 5 minutes without crashes
2. ✅ Both clients complete successfully
3. ✅ No "CONNECTION LOST" errors
4. ✅ All GetHealthParameters requests succeed
5. ✅ FRMC logs show no `InvalidOperationException`

---

**Created:** 2026-02-03
**Purpose:** Validate FRMC channel switching crash fixes
**Status:** Ready for testing
