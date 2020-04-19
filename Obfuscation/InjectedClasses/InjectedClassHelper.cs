﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace RoslynObfuscator.Obfuscation.InjectedClasses
{
    public static class InjectedClassHelper
    {
        public enum InjectableClasses
        {
            StringEncryptor,
            IndirectObjectLoader
        }
        public static string AssemblyDirectory
        {
            get
            {
                string codeBase = Assembly.GetExecutingAssembly().CodeBase;
                UriBuilder uri = new UriBuilder(codeBase);
                string path = Uri.UnescapeDataString(uri.Path);
                return Path.GetDirectoryName(path);
            }
        }

        public static string GetInjectableClassSourceText(InjectableClasses injectableClass)
        {
            string curDirectory = AssemblyDirectory;
            string pathToRead = curDirectory + Path.DirectorySeparatorChar + 
                                "Obfuscation" + Path.DirectorySeparatorChar +
                                "InjectedClasses" + Path.DirectorySeparatorChar;
            switch (injectableClass)
            {
                case InjectableClasses.IndirectObjectLoader:
                    pathToRead += "IndirectObjectLoader.cs";
                    break;
                case InjectableClasses.StringEncryptor:
                    pathToRead += "StringEncryptor.cs";
                    break;
                default:
                    throw new ArgumentException("Unknown Injectable Class Path to Fetch");
            }

            return File.ReadAllText(pathToRead);
        }
    }
}
