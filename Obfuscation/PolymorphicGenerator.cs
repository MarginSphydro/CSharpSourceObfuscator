﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.Remoting.Messaging;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace RoslynObfuscator.Obfuscation
{ 
    public enum RandomStringMethod
    {
        StringFromFixedDictionary,
        StringFromWordlistFile
    }

    public sealed class PolymorphicCodeOptions
    {
        public static PolymorphicCodeOptions Default { get; } = new PolymorphicCodeOptions();

        public RandomStringMethod RandomStringMethod;

        public string CustomStringDictionary;
        public string WordlistPath;

        public int MinimumRandomStringLength;
        public int MaximumRandomStringLength;

        public PolymorphicCodeOptions(
            RandomStringMethod method = RandomStringMethod.StringFromFixedDictionary,
            int minimumRandomStringLength = 10,
            int maximumRandomStringLength = 10,
            string customStringDictionary = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789",
            string wlp = "C:\\tmp\\wordlist.txt")
        {
            RandomStringMethod = method;
            MaximumRandomStringLength = maximumRandomStringLength;
            MinimumRandomStringLength = minimumRandomStringLength;
            CustomStringDictionary = customStringDictionary;
            WordlistPath = wlp;
        }


    }

    public static class PolymorphicGenerator
    {
        private static Random random = new Random();
        private static List<string> words = new List<string>();
        private static HashSet<string> usedWords = new HashSet<string>();

        public static string GetRandomIdentifier()
        {
            return GetRandomIdentifier(PolymorphicCodeOptions.Default);
        }

        public static string GetRandomIdentifier(PolymorphicCodeOptions options)
        {
            string randomIdentifier;

            for (int attempt = 0; attempt < 100; attempt += 1)
            {
                switch (options.RandomStringMethod)
                {
                    case RandomStringMethod.StringFromFixedDictionary:
                        randomIdentifier = BuildRandomStringFromDictionary(options);
                        break;
                    case RandomStringMethod.StringFromWordlistFile:
                        randomIdentifier = GetRandomStringFromWordlist(options);
                        break;
                    default:
                        throw new ArgumentException("RandomStringMethod {0} is Not Supported", options.RandomStringMethod.ToString());
                }

                //Make sure we're generating a valid identifier and it hasn't been used before
                if (Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(randomIdentifier) && !usedWords.Contains(randomIdentifier))
                {
                    usedWords.Add(randomIdentifier);
                    return randomIdentifier;
                }
            }

            // If we've exhausted attempts with wordlist, fall back to dictionary method
            randomIdentifier = BuildRandomStringFromDictionary(options);
            usedWords.Add(randomIdentifier);
            return randomIdentifier;
        }

        public static string GetRandomString()
        {
                return GetRandomString(PolymorphicCodeOptions.Default);
        }

        public static string GetRandomString(PolymorphicCodeOptions options)
        {
            switch (options.RandomStringMethod)
            {
                case RandomStringMethod.StringFromFixedDictionary:
                    return BuildRandomStringFromDictionary(options);
                case RandomStringMethod.StringFromWordlistFile:
                    return GetRandomStringFromWordlist(options);
                default:
                    throw new ArgumentException("RandomStringMethod {0} is Not Supported", options.RandomStringMethod.ToString());
            }
        }

        private static string BuildRandomStringFromDictionary(PolymorphicCodeOptions options)
        {

            int length = random.Next(options.MinimumRandomStringLength,
                options.MaximumRandomStringLength);
            string dictionary = options.CustomStringDictionary;

            return new string(Enumerable.Repeat(dictionary, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());

        }

        private static int genRandom(int length)
        {
            RandomNumberGenerator rng = new RNGCryptoServiceProvider();
            byte[] rndbytes = new byte[4];
            rng.GetBytes(rndbytes);
            uint i = BitConverter.ToUInt32(rndbytes, 0);

            return Convert.ToInt32(i % length);
        }
        private static bool ContainsAllWhitelistedCharacters(string input)
        {
            string whitelist = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";
            foreach (char c in input)
            {
                if (whitelist.IndexOf(c) == -1)
                    return false;
            }
            return true;
        }
        private static string ScrubbingFunction(string input)
        {
            string whitelist = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";
            string ret = "";
            foreach (char c in input)
            {
                if (whitelist.IndexOf(c) != -1)
                    ret += c;
            }
            return ret;
        }

        private static string GetRandomStringFromWordlist(PolymorphicCodeOptions options)
        {
            if(words.Count < 1)
            {
                BuildRandomStringFromDictionaryFile(options);
            }

            // Get available words (not yet used)
            var availableWords = words.Where(w => !usedWords.Contains(w)).ToList();
            if (availableWords.Count < 2)
            {
                // If we're running out of words, fall back to dictionary method
                return BuildRandomStringFromDictionary(options);
            }

            int length = random.Next(options.MinimumRandomStringLength, options.MaximumRandomStringLength);
            string ret = "";

            if (random.Next(3) == 0) // Changed from index % 3 to make it more random
            {
                // Get three unique words
                var selectedWords = new List<string>();
                for (int i = 0; i < 3; i++)
                {
                    var remainingWords = availableWords.Where(w => !selectedWords.Contains(w)).ToList();
                    if (remainingWords.Count == 0) break;
                    
                    int index = genRandom(remainingWords.Count);
                    selectedWords.Add(remainingWords[index]);
                }

                ret = string.Join("_", selectedWords);
            }
            else
            {
                // Get two unique words
                var selectedWords = new List<string>();
                for (int i = 0; i < 2; i++)
                {
                    var remainingWords = availableWords.Where(w => !selectedWords.Contains(w)).ToList();
                    if (remainingWords.Count == 0) break;
                    
                    int index = genRandom(remainingWords.Count);
                    selectedWords.Add(remainingWords[index]);
                }

                ret = string.Join("", selectedWords);
            }

            return ret;
        }
        private static void BuildRandomStringFromDictionaryFile(PolymorphicCodeOptions options)
        {
            //string fileText = File.ReadAllText("C:\\Users\\User\\Documents\\mywordlist.txt");
            string fileText = File.ReadAllText(options.WordlistPath);
            //newline split into string array
            string[] raw_words = fileText.Split('\n');

            for (int i = 0; i < raw_words.Length; ++i)
            {
                if (ContainsAllWhitelistedCharacters(raw_words[i])&& !words.Contains(raw_words[i]) )
                {
                    words.Add(raw_words[i]);  
                }
                if(raw_words[i].Length >= options.MinimumRandomStringLength && raw_words[i].Length < options.MaximumRandomStringLength)
                {
                    string t = ScrubbingFunction(raw_words[i]);
                    if (!words.Contains(t))
                    {
                        words.Add(t);
                    }
                }
            }

        }

    }
}
