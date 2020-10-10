namespace libslzsharp2
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using System.Text;
    using BenchmarkDotNet.Attributes;
    using BenchmarkDotNet.Running;

    public class Benchmarks
    {
        [Benchmark]
        public void GzipStream()
        {
            byte[] input = File.ReadAllBytes(@".\samples\json\governmentroles.json"); 
            // byte[] input = File.ReadAllBytes(@".\samples\calgary\bib");

            using (MemoryStream ms = new MemoryStream())
            {
                using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress))
                {
                    gz.Write(input, 0, input.Length);                    
                }
            }
        }



        [Benchmark]
        public void NormalMemMatch()
        {
            SlzState state = new SlzState(1, SlzState.SlzFormat.SLZ_FMT_GZIP);

            byte[] input = File.ReadAllBytes(@".\samples\json\governmentroles.json");
            // byte[] input = File.ReadAllBytes(@".\samples\calgary\bib");
            byte[] output = new byte[input.Length * 4]; // worst case eh :)

            using (MemoryStream temp = new MemoryStream())
            {
                int lenc = (int) SlzState.slz_encode(state, output, input, input.Length, false);

                // and dump
                temp.Write(output, 0, lenc);
            }
        }
    }
}