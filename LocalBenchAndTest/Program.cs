namespace libslzsharp2
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Text;

    class Program
    {
        static void Main(string[] args)
        {
            // SimpleCase1();


            //var summary = BenchmarkRunner.Run<Benchmarks>();
            //return;

            for (int i = 0; i < 100; i++)
            {

                SlzState state = new SlzState(1, SlzState.SlzFormat.SLZ_FMT_GZIP);
                int total = 0;

                using (FileStream fs = new FileStream(@".\samples\large-calgary\paper4", FileMode.Open, FileAccess.Read))
                // using (FileStream fs = new FileStream(@".\samples\json\governmentroles.json", FileMode.Open, FileAccess.Read))
                {
                    byte[] input = new byte[128 * 1024];
                    byte[] output = new byte[input.Length * 4]; // worst case eh :)

                    using (MemoryStream temp = new MemoryStream())
                    {
                        int read = fs.Read(input, 0, input.Length);

                        while (read > 0)
                        {
                            // compress
                            int lenc = (int)SlzState.slz_encode(state, output, input, read, true);

                            total += lenc;

                            // and dump
                            temp.Write(output, 0, lenc);

                            break;

                            read = fs.Read(input, 0, input.Length);
                        }




                        int flushed = SlzState.slz_finish(state, output);
                        total += flushed;
                        temp.Write(output, 0, flushed);


                        File.WriteAllBytes(@"c:\temp\2.gz", temp.ToArray());


                        // we are done and try to decompress it now
                        using (GZipStream gz = new GZipStream(new MemoryStream(temp.ToArray()), CompressionMode.Decompress))
                        {
                            byte[] decompress = new byte[1024];

                            int totalread;
                            int unread;
                            totalread = unread = gz.Read(decompress, 0, decompress.Length);


                            while (unread > 0)
                            {
                                unread = gz.Read(decompress, 0, decompress.Length);

                                totalread += unread;
                            }
                        }
                    }

                }
            }
        }

        // this is to test multiple input buffers
        static void FileCase1()
        {

        }

        static void SimpleCase1()
        {
            SlzState state = new SlzState(1, SlzState.SlzFormat.SLZ_FMT_GZIP);

            byte[] input = Encoding.UTF8.GetBytes("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

            using (MemoryStream ms = new MemoryStream())
            {

                byte[] output = new byte[1024];
                byte[] output2 = new byte[1024];

                long encoded = 0;

                for (int i = 0; i < input.Length; i++)
                {
                    int lenc = (int)SlzState.slz_encode(state, output, new byte[] { input[i] }, 1, true);

                    if (lenc > 0)
                    {
                        encoded += lenc;
                        ms.Write(output, 0, (int)lenc);
                    }

                }


                long flushed = SlzState.slz_finish(state, output2);

                ms.Write(output2, 0, (int)flushed);


                byte[] lala = ms.ToArray();




                string s = Convert.ToBase64String(lala);


                //using (GZipStream gz = new GZipStream(new MemoryStream(lala), CompressionMode.Decompress))
                //{
                //    byte[] decompress = new byte[1024];

                //    gz.Read(decompress, 0, decompress.Length);

                //}

                using (GZipStream gz = new GZipStream(new MemoryStream(lala), CompressionMode.Decompress))
                {
                    byte[] decompress = new byte[1024];

                    gz.Read(decompress, 0, decompress.Length);

                }
            }
        }
    }
}










