using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text; // Required for device name string conversion

/// <summary>
/// Container for general test parameters and configuration flags.
/// </summary>
public class Flags
{
    // Maximum size for the device name buffer, as defined in the native code.
    public const int MAX_DEVICE_NAME = 256;

    // --- Input Parameters for Initialize ---
    public uint BufferSize { get; set; } // The total length of one input signal buffer.
    public uint Columns { get; set; }    // The number of Doppler bins (X dimension).
    public uint Rows { get; set; }       // The number of Time delay bins (Y dimension).
    public double DopplerZoom { get; set; }
    public string DeviceName { get; set; } = "";

    // --- Input Parameters for Run ---
    public float PasiveGain { get; set; }
    public int DistanceShift { get; set; }
    public short ScaleType { get; set; }
    public bool RemoveSymmetrics { get; set; }
}

/// <summary>
/// Static class to manage all P/Invoke declarations for Ambiguity.dll.
/// </summary>
public static class Ambiguity
{
    // --- General Setup/Teardown Functions ---

    [DllImport(@"Ambiguity.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Initialize(uint BufferSize, uint col, uint row, float doopler_shift, [Out] short[] Name);

    [DllImport(@"Ambiguity.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int Release();

    // --- Pinned Memory Management Functions (For Pinned Memory Testing) ---

    [DllImport(@"Ambiguity.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int AllocHostMemory(uint bufferSize, out IntPtr hostPtr0, out IntPtr hostPtr1);

    [DllImport(@"Ambiguity.dll", CallingConvention = CallingConvention.Cdecl)]
    public static extern int FreeHostMemory(IntPtr hostPtr0, IntPtr hostPtr1);

    // --- P/Invoke Signatures for Run Function (Memory Type Specific) ---

    /// <summary>
    /// P/Invoke declarations for the standard Heap Memory version (using C# arrays).
    /// </summary>
    public static class Heap
    {
        [DllImport(@"Ambiguity.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Run(
            [In] float[] Data_In0,
            [In] float[] Data_In1,
            [Out] float[] Data_Out,
            float amplification,
            float doppler_zoom,
            int shift,
            bool mode,
            short scale_type,
            bool remove_symetric
        );
    }

    /// <summary>
    /// P/Invoke declarations for the Pinned Memory version (using IntPtr pointers).
    /// </summary>
    public static class Pinned
    {
        // NOTE: Pinned memory requires IntPtr to be passed for the input data buffers.
        [DllImport(@"Ambiguity.dll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Run(
            IntPtr Data_In0,
            IntPtr Data_In1,
            [Out] float[] Data_Out,
            float amplification,
            float doppler_zoom,
            int shift,
            bool mode,
            short scale_type,
            bool remove_symetric
        );
    }
}

/// <summary>
/// Central class for running performance tests using both memory types.
/// </summary>
public class PerformanceTester
{
    private static Random random = new Random();

    /// <summary>
    /// Helper to generate random data and copy it into the Pinned Memory region.
    /// This is necessary because C# cannot directly access IntPtr memory.
    /// </summary>
    private static void FillPinnedMemory(IntPtr hostPtr, float[] tempArray, int size)
    {
        // 1. Fill a standard C# array with random data.
        for (int i = 0; i < size; i++)
        {
            // Generate random values between -1.0f and 1.0f
            tempArray[i] = (float)(random.NextDouble() * 2.0 - 1.0);
        }

        // 2. Copy data from the C# array to the Pinned Memory (IntPtr) using Marshal.
        Marshal.Copy(tempArray, 0, hostPtr, size);
    }

    /// <summary>
    /// Executes the performance test for a specified memory type.
    /// </summary>
    /// <param name="testIterations">The number of times to call the Run function.</param>
    /// <param name="flags">The configuration parameters for the test.</param>
    /// <param name="usePinnedMemory">If true, uses the Pinned Memory implementation.</param>
    public static void RunTest(int testIterations, Flags flags, bool usePinnedMemory)
    {
        string memoryType = usePinnedMemory ? "Pinned Memory" : "Standard Heap";
        Console.WriteLine($"\n--- Starting Performance Test: {memoryType} ---");
        Console.WriteLine($"Parameters: BufferSize={flags.BufferSize}, Columns={flags.Columns}, Rows={flags.Rows}");

        // Calculate buffer sizes
        int inputSize = (int)(flags.BufferSize * 2); // Assuming float2 complex data (2 floats) per sample
        int outputSize = (int)(flags.Rows * flags.Columns + flags.Rows); // Output buffer size (Ambiguity Map)

        // 1. Initialize the native library
        short[] deviceNameBuffer = new short[Flags.MAX_DEVICE_NAME];
        int initResult = Ambiguity.Initialize(flags.BufferSize, flags.Columns, flags.Rows, (float)flags.DopplerZoom, deviceNameBuffer);
        if (initResult != 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FATAL ERROR] Failed to initialize Ambiguity.dll. Error Code: {initResult}");
            Console.ResetColor();
            return;
        }

        // Convert device name from short[] to string (typical for native C++ device strings)
        flags.DeviceName = Encoding.ASCII.GetString(deviceNameBuffer.Select(s => (byte)s).TakeWhile(b => b != 0).ToArray());
        Console.WriteLine($"[INFO] Library initialized successfully on Device: {flags.DeviceName}");


        // 2. Memory Setup: Allocate buffers based on memory type
        IntPtr dataIn0Ptr = IntPtr.Zero;
        IntPtr dataIn1Ptr = IntPtr.Zero;

        // C# float arrays used for either: 1) standard heap data, or 2) temporary data for Pinned Memory Marshal.Copy
        float[] dataIn0 = new float[inputSize];
        float[] dataIn1 = new float[inputSize];
        float[] dataOut = new float[outputSize];


        if (usePinnedMemory)
        {
            int allocResult = Ambiguity.AllocHostMemory((uint)inputSize, out dataIn0Ptr, out dataIn1Ptr);

            if (allocResult != 0 || dataIn0Ptr == IntPtr.Zero || dataIn1Ptr == IntPtr.Zero)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[FATAL ERROR] Failed to allocate Pinned Memory. Error Code: {allocResult}");
                Console.ResetColor();
                Ambiguity.Release();
                return;
            }

            Console.WriteLine($"[INFO] Pinned Memory allocated successfully. Generating and copying data...");
            // Copy data from the C# array (dataIn0/1) to the Pinned Memory pointer (dataIn0Ptr/1Ptr)
            FillPinnedMemory(dataIn0Ptr, dataIn0, inputSize);
            FillPinnedMemory(dataIn1Ptr, dataIn1, inputSize);
        }
        else
        {
            // For standard Heap Memory, just fill the C# arrays directly
            Console.WriteLine($"[INFO] Using Standard Heap Memory. Generating data...");
            for (int i = 0; i < inputSize; i++)
            {
                dataIn0[i] = (float)(random.NextDouble() * 2.0 - 1.0);
                dataIn1[i] = (float)(random.NextDouble() * 2.0 - 1.0);
            }
        }


        // 3. Performance Measurement
        Stopwatch stopwatch = new Stopwatch();
        long totalTimeMs = 0;

        Console.WriteLine($"[TEST] Executing {testIterations} iterations of Run function...");

        // --- Warm-up Call ---
        // The first call is often slow due to device initialization/JIT compilation.
        if (usePinnedMemory)
        {
            Ambiguity.Pinned.Run(dataIn0Ptr, dataIn1Ptr, dataOut, flags.PasiveGain, (float)flags.DopplerZoom, flags.DistanceShift, false, flags.ScaleType, flags.RemoveSymmetrics);
        }
        else
        {
            Ambiguity.Heap.Run(dataIn0, dataIn1, dataOut, flags.PasiveGain, (float)flags.DopplerZoom, flags.DistanceShift, false, flags.ScaleType, flags.RemoveSymmetrics);
        }


        // --- Timed Loop ---
        for (int i = 0; i < testIterations; i++)
        {
            stopwatch.Restart();
            int runResult = usePinnedMemory
                ? Ambiguity.Pinned.Run(dataIn0Ptr, dataIn1Ptr, dataOut, flags.PasiveGain, (float)flags.DopplerZoom, flags.DistanceShift, false, flags.ScaleType, flags.RemoveSymmetrics)
                : Ambiguity.Heap.Run(dataIn0, dataIn1, dataOut, flags.PasiveGain, (float)flags.DopplerZoom, flags.DistanceShift, false, flags.ScaleType, flags.RemoveSymmetrics);
            stopwatch.Stop();

            if (runResult < 0)
            {
                Console.WriteLine($"[WARNING] Error in iteration {i + 1}. Error Code: {runResult}");
            }

            totalTimeMs += stopwatch.ElapsedMilliseconds;
        }

        // 4. Results
        Console.WriteLine($"\n--- {memoryType} Test Results ---");
        double averageTimeMs = (double)totalTimeMs / testIterations;
        Console.WriteLine($"Total Iterations: {testIterations}");
        Console.WriteLine($"Total Run Time: {totalTimeMs} ms");
        Console.WriteLine($"**Average Run Time:** **{averageTimeMs:F3} ms**");

        // 5. Cleanup
        if (usePinnedMemory)
        {
            Ambiguity.FreeHostMemory(dataIn0Ptr, dataIn1Ptr);
            Console.WriteLine("[INFO] Pinned Memory released successfully.");
        }

        Ambiguity.Release();
        Console.WriteLine("[INFO] Native resources released successfully.");
    }
}

// --- Main Console Application Class ---
public class Program
{
    public static void Main(string[] args)
    {
        // Define constant test parameters
        const int TestIterations = 1000;

        // Define input parameters (Flags)
        Flags testFlags = new Flags
        {
            // NOTE: Adjust these parameters to match your actual data sizes (e.g., 1024x4096)
            BufferSize = 1024* 1024 * 4, // Example: Total number of complex samples
            Columns = 100,        // Doppler bins
            Rows = 100,           // Time delay bins

            // Run Parameters (Example values)
            DopplerZoom = 1000.0,
            PasiveGain = 1.0f,
            DistanceShift = 0,
            ScaleType = 0,
            RemoveSymmetrics = true
        };

        // --- Run Both Tests Sequentially for Comparison ---

        // Test 1: Standard Heap Memory (Marshaling arrays)
        PerformanceTester.RunTest(TestIterations, testFlags, usePinnedMemory: false);

        // Test 2: Pinned Memory (Using IntPtr)
        PerformanceTester.RunTest(TestIterations, testFlags, usePinnedMemory: true);

        Console.WriteLine("\nPress any key to exit...");
        Console.ReadKey();
    }
}