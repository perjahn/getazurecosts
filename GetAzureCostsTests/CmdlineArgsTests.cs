using GetAzureCosts;
using NUnit.Framework;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;

namespace GetAzureCostsTests
{
    public class CmdlineArgsTests
    {
        Random Rand => new Random();
        string AllChars { get; set; }

        [SetUp]
        public void Setup()
        {
            var chars = new StringBuilder();
            for (int c = 32; c < 127; c++)
            {
                chars.Append((char)c);
            }
            AllChars = chars.ToString();
        }

        [Test]
        public void TestArgsContainsNoFlags()
        {
            var args = new List<string>() { "aa", "bb", "cc", "dd" };

            var result = CmdlineArgs.ExtractFlag(args, "-bb");
            Assert.AreEqual(null, result);

            result = CmdlineArgs.ExtractFlag(args, "-bb");
            Assert.AreEqual(null, result);
        }

        [Test]
        public void TestArgsContainsFlags()
        {
            var args = new List<string>() { "aa", "-bb", "cc", "dd" };

            var result = CmdlineArgs.ExtractFlag(args, "-bb", false);
            Assert.AreEqual(args.Count, 4);
            Assert.AreEqual("cc", result);

            result = CmdlineArgs.ExtractFlag(args, "-bb");
            Assert.AreEqual(args.Count, 2);
            Assert.AreEqual("cc", result);

            result = CmdlineArgs.ExtractFlag(args, "-bb");
            Assert.AreEqual(args.Count, 2);
            Assert.AreEqual(null, result);
        }

        [Test]
        public void TestArgsContainsFlags2()
        {
            var args = new List<string>() { "-aa", "-aa", "-aa", "-aa" };

            var result = CmdlineArgs.ExtractFlag(args, "-aa", false);
            Assert.AreEqual(args.Count, 4);
            Assert.AreEqual("-aa", result);

            result = CmdlineArgs.ExtractFlag(args, "-aa");
            Assert.AreEqual(args.Count, 2);
            Assert.AreEqual("-aa", result);

            result = CmdlineArgs.ExtractFlag(args, "-aa");
            Assert.AreEqual(args.Count, 0);
            Assert.AreEqual("-aa", result);

            result = CmdlineArgs.ExtractFlag(args, "-aa");
            Assert.AreEqual(args.Count, 0);
            Assert.AreEqual(null, result);
        }

        [Test]
        public void TestArgsContainsNoFlagsFuzz()
        {
            Random rand = new Random();

            for (var test = 0; test < 10000; test++)
            {
                var args = new List<string>();
                for (var i = 0; i < rand.Next(0, 10); i++)
                {
                    args.Add(GetRandomString().Replace("-", string.Empty));
                }

                var randomFlag = "-" + GetRandomString();

                var result = CmdlineArgs.ExtractFlag(args, randomFlag);

                Assert.AreEqual(null, result);
            }
        }

        [Test]
        public void TestArgsContainsFlagsFuzz()
        {
            for (var test = 0; test < 10000; test++)
            {
                var args = new List<string>();
                for (var i = 0; i < Rand.Next(2, 10); i++)
                {
                    args.Add(GetRandomString());
                }

                var argsCopy = new string[args.Count];
                args.CopyTo(argsCopy);
                var argsCopyList = argsCopy.ToList();

                var randomFlag = args[Rand.Next(0, args.Count - 2)];
                var randomFlagValue = args[args.IndexOf(randomFlag) + 1];

                var result = CmdlineArgs.ExtractFlag(args, randomFlag);

                Assert.AreEqual(randomFlagValue, result, "Original argument list: '{0}'", string.Join("', '", argsCopyList));
            }
        }

        private string GetRandomString()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Rand.Next(0, 10); i++)
            {
                sb.Append(AllChars[Rand.Next(0, AllChars.Length)]);
            }
            return sb.ToString();
        }
    }
}