using System;
using NUnit.Framework;

namespace IL2CXX.Tests
{
    class FinalizerTests
    {
        class Foo : IDisposable
        {
            public static int Finalized;

            string message;
            Foo foo;

            public Foo(bool cyclic)
            {
                if (cyclic)
                {
                    message = "cyclic";
                    foo = this;
                }
                else
                {
                    message = "acyclic";
                }
            }
            ~Foo()
            {
                Console.WriteLine($"~Foo: {message}");
                ++Finalized;
            }
            public void Dispose() => GC.SuppressFinalize(this);
        }
        static int CollectAndWait()
        {
            new Foo(true);
            new Foo(false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Console.WriteLine($"finalized: {Foo.Finalized}");
            return Foo.Finalized == 2 ? 0 : 1;
        }
        [Test]
        public void TestCollectAndWait() => Utilities.Test(CollectAndWait);
        static int Suppress()
        {
            using (new Foo(false)) { }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Console.WriteLine($"finalized: {Foo.Finalized}");
            return Foo.Finalized == 0 ? 0 : 1;
        }
        [Test]
        public void TestSuppress() => Utilities.Test(Suppress);

        class Bar
        {
            public static Bar Resurrected;
            public static int Finalized;

            bool resurrected;

            ~Bar()
            {
                if (resurrected)
                {
                    Console.WriteLine("~Bar: finalize");
                    ++Finalized;
                }
                else
                {
                    Console.WriteLine("~Bar: resurrect");
                    Resurrected = this;
                    resurrected = true;
                    GC.ReRegisterForFinalize(this);
                }
            }
        }
        static int Resurrect()
        {
            new Bar();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Console.WriteLine($"resurrected: {Bar.Resurrected}");
            if (Bar.Resurrected == null) return 1;
            Bar.Resurrected = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Console.WriteLine($"finalized: {Bar.Finalized}");
            return Bar.Finalized == 1 ? 0 : 1;
        }
        [Test]
        public void TestResurrect() => Utilities.Test(Resurrect);
    }
}