using System;
using Cloo;
using System.Drawing;
using System.Drawing.Imaging;
using OpenSatelliteProject.Geo;
using OpenSatelliteProject;
using OpenSatelliteProject.PacketData;
using System.Text.RegularExpressions;
using System.Globalization;
using OpenSatelliteProject.Tools;
using System.IO;
using Cloo.Bindings;

namespace CLReproject {
    class MainClass {
        // static string irfilename = "./OR_ABI-L2-CMIPF-M3C13_G16_s20170861545382_e20170861556160_c20170861556231.lrit";
        static string visFilename = "./OR_ABI-L2-CMIPF-M3C02_G16_s20170861545382_e20170861556149_c20170861556217.lrit";
        static GeoConverter gc;
        static Bitmap bmp;

        public static void Main (string[] args) {
            LoadLRIT ();
            ComputePlatform[] availablePlatforms = new ComputePlatform[ComputePlatform.Platforms.Count];
            for (int i = 0; i < availablePlatforms.Length; i++) {
                availablePlatforms [i] = ComputePlatform.Platforms [i];
                Console.WriteLine (ComputePlatform.Platforms [i].Name);
            }

            var platform = availablePlatforms [0];

            ComputeContextPropertyList properties = new ComputeContextPropertyList(platform);
            ComputeContext context = new ComputeContext(platform.Devices, properties, null, IntPtr.Zero);
            CLProcess(context);
            UIConsole.Log ("Finish");
        }

        static float satelliteLongitude;
        static bool fixAspect = false;
        static float aspectRatio = 1;
        static float[] latRange;
        static float[] lonRange;
        static float[] coverage;
        static float[] trim;
        static uint[] size;
        static int coff;
        static float cfac;
        static int loff;
        static float lfac;

        public static void LoadLRIT() {
            UIConsole.Log($"Loading Headers from Visible file at {visFilename}");
            XRITHeader header = FileParser.GetHeaderFromFile(visFilename);
            Regex x = new Regex(@".*\((.*)\)", RegexOptions.IgnoreCase);
            var regMatch = x.Match(header.ImageNavigationHeader.ProjectionName);
            satelliteLongitude = float.Parse(regMatch.Groups[1].Captures[0].Value, CultureInfo.InvariantCulture);
            var inh = header.ImageNavigationHeader;
            gc = new GeoConverter(satelliteLongitude, inh.ColumnOffset, inh.LineOffset, inh.ColumnScalingFactor, inh.LineScalingFactor);
            var od = new OrganizerData();
            od.Segments.Add(0, visFilename);
            od.FirstSegment = 0;
            od.Columns = header.ImageStructureHeader.Columns;
            od.Lines = header.ImageStructureHeader.Lines;
            od.ColumnOffset = inh.ColumnOffset;
            od.PixelAspect = 1;
            UIConsole.Log($"Generating Visible Bitmap");
            bmp = ImageTools.GenerateFullImage(od);
            // bmp = ImageTools.ResizeImage (bmp, bmp.Width / 4, bmp.Height / 4, true);
            if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
                bmp = bmp.ToFormat (PixelFormat.Format32bppArgb, true);

            latRange = new[] { gc.MinLatitude, gc.MaxLatitude };
            lonRange = new[] { gc.MinLongitude, gc.MaxLongitude };
            coverage = new[] { gc.LatitudeCoverage, gc.LongitudeCoverage };
            trim = new[] { gc.TrimLatitude, gc.TrimLongitude };
            size = new[] { (uint)bmp.Width, (uint)bmp.Height };
            coff = inh.ColumnOffset;
            loff = inh.LineOffset;
            cfac = inh.ColumnScalingFactor;
            lfac = inh.LineScalingFactor;
        }

        public static void CLProcess(ComputeContext context) {
            try {
                UIConsole.Log ("Loading kernel.cl");
                var clProgramSource = File.ReadAllText ("kernel.cl");
                UIConsole.Log ("Compiling kernel");
                var program = new ComputeProgram(context, clProgramSource);
                try {
                    program.Build(null, null, null, IntPtr.Zero);
                } catch (Exception e) {
                    UIConsole.Error("Build Log" + program.GetBuildLog(context.Devices[0]));
                    throw e;
                }
                ComputeKernel kernel = program.CreateKernel("reproject");
                var startTime = LLTools.TimestampMS();
                ComputeImageFormat format = new ComputeImageFormat(ComputeImageChannelOrder.Bgra, ComputeImageChannelType.UnsignedInt8);
                UIConsole.Log ("Loading Bitmaps");
                BitmapData bitmapData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
                ComputeImage2D source = new ComputeImage2D (context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, format, bmp.Width, bmp.Height, bitmapData.Stride, bitmapData.Scan0);
                ComputeImage2D output = new ComputeImage2D (context, ComputeMemoryFlags.ReadWrite | ComputeMemoryFlags.AllocateHostPointer, format, bmp.Width, bmp.Height, 0, IntPtr.Zero);
                bmp.UnlockBits(bitmapData);

                UIConsole.Log ("Loading Images to kernel");

                //  0    __read_only  image2d_t srcImg, 
                //  1   __write_only image2d_t dstImg,
                //  2   float satelliteLongitude, 
                //  3   int coff, 
                //  4   float cfac, 
                //  5   int loff, 
                //  6   float lfac,
                //  7   bool fixAspect,
                //  8   float aspectRatio,
                //  9   float2 latRange,
                //  10  float2 lonRange,
                //  11  float2 coverage,
                //  12  float2 trim,
                //  13  uint2 size,

                kernel.SetMemoryArgument (0, source);
                kernel.SetMemoryArgument (1, output);
                kernel.SetValueArgument (2, satelliteLongitude);
                kernel.SetValueArgument (3, coff);
                kernel.SetValueArgument (4, cfac);
                kernel.SetValueArgument (5, loff);
                kernel.SetValueArgument (6, lfac);
                kernel.SetValueArgument (7, (uint) (fixAspect ? 1 : 0));
                kernel.SetValueArgument (8, aspectRatio);

                var latRangeBuff = new ComputeBuffer<float>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, latRange);
                var lonRangeBuff = new ComputeBuffer<float>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, lonRange);
                var coverageBuff = new ComputeBuffer<float>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, coverage);
                var trimBuff = new ComputeBuffer<float>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, trim);
                var sizeBuff = new ComputeBuffer<uint>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, size);

                kernel.SetMemoryArgument (9, latRangeBuff);
                kernel.SetMemoryArgument (10, lonRangeBuff);
                kernel.SetMemoryArgument (11, coverageBuff);
                kernel.SetMemoryArgument (12, trimBuff);
                kernel.SetMemoryArgument (13, sizeBuff);

                ComputeEventList eventList = new ComputeEventList();

                // Create the command queue. This is used to control kernel execution and manage read/write/copy operations.
                ComputeCommandQueue commands = new ComputeCommandQueue(context, context.Devices[0], ComputeCommandQueueFlags.None);

                UIConsole.Log ("Executing kernel");
                commands.Execute(kernel, null, new long[] { bmp.Width, bmp.Height }, null, eventList);

                UIConsole.Log ("Dumping bitmap");
                Bitmap obmp = new Bitmap(bmp.Width, bmp.Height, bmp.PixelFormat);
                BitmapData bmpData = obmp.LockBits(new Rectangle(0, 0, obmp.Width, obmp.Height), ImageLockMode.ReadWrite, obmp.PixelFormat);
                commands.ReadFromImage(output, bmpData.Scan0, true, null);
                obmp.UnlockBits (bmpData);
                var delta = LLTools.TimestampMS() - startTime;
                UIConsole.Log($"Took {delta} ms to reproject!");
                UIConsole.Log ("Saving bitmap");
                obmp.Save ("teste.png");
                UIConsole.Log ("Done");
                bmp.Save ("original.png");

                source.Dispose();
                output.Dispose();
                commands.Dispose();
                kernel.Dispose();
                program.Dispose();
            } catch (Exception e) {
                UIConsole.Error ($"Error executing: {e.ToString()}");
            }
        }
    }
}
