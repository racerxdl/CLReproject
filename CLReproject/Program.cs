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
using System.Reflection;
using System.Linq;

namespace CLReproject {
    class MainClass {
        static string irfilename = "./OR_ABI-L2-CMIPF-M3C13_G16_s20170861545382_e20170861556160_c20170861556231.lrit";
        static string visFilename = "./OR_ABI-L2-CMIPF-M3C02_G16_s20170861545382_e20170861556149_c20170861556217.lrit";
        static GeoConverter gc;
        static Bitmap bmp;
        static Bitmap irBmp;

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
            CLBuilder(context);
            CLApply2DLUT(context);
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
            if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
                bmp = bmp.ToFormat (PixelFormat.Format32bppArgb, true);

            // 
            od.Segments[0] = irfilename;
            irBmp = ImageTools.GenerateFullImage(od);           
            if (bmp.PixelFormat != PixelFormat.Format32bppArgb)
                bmp = bmp.ToFormat (PixelFormat.Format32bppArgb, true);
            // Geo Converter
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

        static ComputeProgram program;
        static ComputeKernel reprojectKernel;
        static ComputeKernel apply2DLUTKernel;
        static ComputeKernel applyCurveKernel;

        static byte[] curveLut;
        static ComputeBuffer<byte> curveLutBuffer;

        static uint[] lut2D;
        static ComputeBuffer<uint> lut2DBuffer;

        public static void CLBuilder(ComputeContext context) {
            UIConsole.Log ("Loading kernel.cl");
            var clProgramSource = File.ReadAllText ("kernel.cl");
            UIConsole.Log ("Compiling kernel");
            program = new ComputeProgram(context, clProgramSource);
            try {
                program.Build(null, null, null, IntPtr.Zero);
            } catch (Exception e) {
                UIConsole.Error("Build Log: \n" + program.GetBuildLog(context.Devices[0]));
                throw e;
            }

            reprojectKernel = program.CreateKernel("reproject");
            apply2DLUTKernel = program.CreateKernel("apply2DLUT");
            applyCurveKernel = program.CreateKernel("applyCurve");

            UIConsole.Log ("Building Curve LUT");
            curveLut = new byte[256];
            for (int i = 0; i < 256; i++) {
                float v = 255 * OpenSatelliteProject.Presets.NEW_VIS_FALSE_CURVE[i];
                curveLut[i] = (byte) (((int)Math.Floor(v)) & 0xFF);
            }
            curveLutBuffer = new ComputeBuffer<byte>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, curveLut);

            UIConsole.Log ("Loading LUT2D");
            byte[] buffer = ReadFileFromOSPAssembly("falsecolor.png");
            Bitmap lutBmp;
            using (MemoryStream stream = new MemoryStream(buffer)) {
                lutBmp = new Bitmap(stream);
            }
            lut2D = new uint[256 * 256];
            for (int i = 0; i < 256; i++) {
                for (int j = 0; j < 256; j++) {
                    lut2D[(i * 256) + j] = (uint)(lutBmp.GetPixel(j, i).ToArgb() & 0xFFFFFFFF);
                }
            }
            lut2DBuffer = new ComputeBuffer<uint>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, lut2D);
        }

        public static void CLApply2DLUT(ComputeContext context) {
            ComputeImageFormat format = new ComputeImageFormat(ComputeImageChannelOrder.Bgra, ComputeImageChannelType.UnsignedInt8);
            var startTime = LLTools.TimestampMS ();
            #region Visible / Temporary Source
            BitmapData bitmapData = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
            ComputeImage2D source0 = new ComputeImage2D (context, ComputeMemoryFlags.ReadWrite | ComputeMemoryFlags.CopyHostPointer, format, bmp.Width, bmp.Height, bitmapData.Stride, bitmapData.Scan0);
            bmp.UnlockBits(bitmapData);
            #endregion
            #region Infrared Source
            bitmapData = irBmp.LockBits(new Rectangle(0, 0, irBmp.Width, irBmp.Height), ImageLockMode.ReadOnly, irBmp.PixelFormat);
            ComputeImage2D source1 = new ComputeImage2D (context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, format, irBmp.Width, irBmp.Height, bitmapData.Stride, bitmapData.Scan0);
            irBmp.UnlockBits(bitmapData);
            #endregion
            #region Output
            ComputeImage2D output = new ComputeImage2D (context, ComputeMemoryFlags.ReadWrite | ComputeMemoryFlags.AllocateHostPointer, format, bmp.Width, bmp.Height, 0, IntPtr.Zero);
            #endregion
            #region Variable Initialization
            ComputeEventList eventList = new ComputeEventList();
            ComputeCommandQueue commands = new ComputeCommandQueue(context, context.Devices[0], ComputeCommandQueueFlags.None);
            #region Apply Curve
            applyCurveKernel.SetMemoryArgument (0, source0);
            applyCurveKernel.SetMemoryArgument (1, output);
            applyCurveKernel.SetMemoryArgument (2, curveLutBuffer);
            #endregion
            #region Apply LUT 2D
            apply2DLUTKernel.SetMemoryArgument (0, source1);
            apply2DLUTKernel.SetMemoryArgument (1, output);
            apply2DLUTKernel.SetMemoryArgument (2, source0);
            apply2DLUTKernel.SetMemoryArgument (3, lut2DBuffer);
            #endregion
            #region Reprojection
            var latRangeBuff = new ComputeBuffer<float>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, latRange);
            var lonRangeBuff = new ComputeBuffer<float>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, lonRange);
            var coverageBuff = new ComputeBuffer<float>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, coverage);
            var trimBuff = new ComputeBuffer<float>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, trim);
            var sizeBuff = new ComputeBuffer<uint>(context, ComputeMemoryFlags.ReadOnly | ComputeMemoryFlags.CopyHostPointer, size);
            reprojectKernel.SetMemoryArgument (0, source0);
            reprojectKernel.SetMemoryArgument (1, output);
            reprojectKernel.SetValueArgument (2, satelliteLongitude);
            reprojectKernel.SetValueArgument (3, coff);
            reprojectKernel.SetValueArgument (4, cfac);
            reprojectKernel.SetValueArgument (5, loff);
            reprojectKernel.SetValueArgument (6, lfac);
            reprojectKernel.SetValueArgument (7, (uint) (fixAspect ? 1 : 0));
            reprojectKernel.SetValueArgument (8, aspectRatio);
            reprojectKernel.SetMemoryArgument (9, latRangeBuff);
            reprojectKernel.SetMemoryArgument (10, lonRangeBuff);
            reprojectKernel.SetMemoryArgument (11, coverageBuff);
            reprojectKernel.SetMemoryArgument (12, trimBuff);
            reprojectKernel.SetMemoryArgument (13, sizeBuff);
            #endregion
            #endregion
            #region Run Pipeline
            UIConsole.Log ("Executing curve kernel");
            commands.Execute(applyCurveKernel, null, new long[] { bmp.Width, bmp.Height }, null, eventList);
            UIConsole.Log ("Executing LUT2D kernel");
            commands.Execute(apply2DLUTKernel, null, new long[] { bmp.Width, bmp.Height }, null, eventList);
            UIConsole.Log ("Executing kernel");
            commands.Execute(reprojectKernel, null, new long[] { bmp.Width, bmp.Height }, null, eventList);
            #endregion
            #region Dump Bitmap
            UIConsole.Log ("Dumping bitmap");
            Bitmap obmp = new Bitmap(bmp.Width, bmp.Height, bmp.PixelFormat);
            BitmapData bmpData = obmp.LockBits(new Rectangle(0, 0, obmp.Width, obmp.Height), ImageLockMode.ReadWrite, obmp.PixelFormat);
            commands.ReadFromImage(output, bmpData.Scan0, true, null);
            obmp.UnlockBits (bmpData);

            var delta = LLTools.TimestampMS() - startTime;
            UIConsole.Log($"Took {delta} ms to Apply Curve -> Apply Lut2D (FalseColor) -> Reproject");
            UIConsole.Log ("Saving bitmap");
            obmp.Save ("teste.png");
            UIConsole.Log ("Done");
            bmp.Save ("original.png");
            #endregion
        }

        // From XRITTune https://github.com/opensatelliteproject/XRITTune/blob/master/XRITTune/Tools.cs

        public static Assembly GetAssemblyByName(string name) {
            return AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(assembly => assembly.GetName().Name == name);
        }

        public static byte[] ReadFileFromOSPAssembly(string filename) {
            byte[] buffer = null;
            Assembly xritAssembly = GetAssemblyByName("XRIT");
            try {
                using (Stream stream = xritAssembly.GetManifestResourceStream(string.Format("OpenSatelliteProject.LUT.{0}", filename))) {
                    int num2;
                    buffer = new byte[stream.Length];
                    for (int i = 0; i < stream.Length; i += num2) {
                        num2 = ((stream.Length - i) > 0x1000L) ? 0x1000 : (((int)stream.Length) - i);
                        stream.Read(buffer, i, num2);
                    }
                }
            } catch (Exception) {
                UIConsole.Warn(string.Format("Cannot load {0} from library.", filename));
            }
            return buffer;
        }
    }
}
