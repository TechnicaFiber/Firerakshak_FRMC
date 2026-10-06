# Quick Start Guide - FRMC Load Test

## ⚡ Fastest Way to Run

### Windows:
```cmd
cd "c:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL\FRMC.LoadTest"
run-test.bat
```

### Linux/Mac:
```bash
cd "c:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL\FRMC.LoadTest"
chmod +x run-test.sh
./run-test.sh
```

---

## 📋 Manual Steps

### 1. Start FRMC (Terminal 1)
```bash
cd "c:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL\FireRakshak.FRMC.Host\bin\Release\net8.0"
dotnet FireRakshak.FRMC.Host.dll
```

Wait for:
```
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

### 2. Run Load Test (Terminal 2)
```bash
cd "c:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL\FRMC.LoadTest"
dotnet run --configuration Release
```

### 3. Monitor FRMC Logs (Terminal 3 - Optional)
```bash
tail -f "c:\FireRakshak\FinalTesting\Fire Rakshak Builds\Fire Rakshak\FRMC-SERIAL\FRMC-SERIAL\FireRakshak.FRMC.Host\bin\Release\net8.0\Logs\frmc-*.log"
```

---

## ✅ What Success Looks Like

You should see output like this:

```
╔═══════════════════════════════════════════════════════════╗
║  FRMC Channel Switching Load Test                        ║
║  Testing: 8 channels × 10km × 5-second scan cycle        ║
╚═══════════════════════════════════════════════════════════╝

🌡️  [DTS] Mock DTS Simulator started
✅ [Client-1] Connected to FRMC WebSocket
✅ [Client-2] Connected to FRMC WebSocket
🔐 [Client-1] Authenticated
▶️  [Client-1] Live measurement started
🔄 [Client-1] Switch #1: Channel 1 → 3
✅ [Client-1] GetHealthParameters succeeded (no timeout!)
📊 [Client-1] Received live data: Channel 3, 25000 points
🔄 [Client-2] Switch #5: Channel 5 → 2
...

✅ Test completed successfully!
```

---

## ❌ What Failure Looks Like

**Bad output (indicates the bug is still present):**
```
❌ [Client-1] CONNECTION LOST - Server closed connection prematurely!
   This indicates the concurrent SendAsync crash occurred!
❌ Test failed with 2 errors
```

**Check FRMC logs for:**
```
[ERR] ❌ WS Send failed!
System.InvalidOperationException: There is already one outstanding 'SendAsync' call
```

---

## 🔧 Troubleshooting

| Problem | Solution |
|---------|----------|
| `Connection refused` | Ensure FRMC is running on port 6164 |
| `Login failed` | Check username/password in test code (default: admin1/Admin@123) |
| Test runs but no channel switches | Check if live measurement started successfully |
| CONNECTION LOST errors | **This means the bug is NOT fixed** - check FRMC logs |

---

## ⚙️ Configuration

Edit `Program.cs` to customize test parameters:

```csharp
const int NUM_CHANNELS = 8;                    // Test 8 channels
const double CHANNEL_LENGTH_KM = 10.0;         // 10km per channel
const int SCAN_INTERVAL_SECONDS = 5;           // DTS scans every 5s
const int TEST_DURATION_MINUTES = 5;           // Run for 5 minutes
```

---

## 📊 Expected Performance

| Metric | Value |
|--------|-------|
| **Data per channel** | 25,000 points |
| **Data per scan cycle** | 200,000 points (8 channels) |
| **Broadcast frequency** | Every 5 seconds |
| **Channel switches** | ~60 switches per client in 5 minutes |
| **GetHealthParameters** | ~60 requests per client |
| **Total messages** | ~2,400+ WebSocket messages |

---

## 🎯 Success Criteria

Test passes if:
- [x] Runs for full 5 minutes without crashes
- [x] Both clients complete successfully
- [x] No "CONNECTION LOST" errors
- [x] All GetHealthParameters succeed within 10 seconds
- [x] FRMC logs show NO `InvalidOperationException`

---

**Ready to test!** Just run `run-test.bat` (Windows) or `run-test.sh` (Linux/Mac)
