using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using static Model.EnumDef;

namespace Common
{
    public class Tools
    {
        public static string GetSP(string symbol, Period p)
        {
            return symbol + "_" + p;
        }

        public static string GetTableName(string tableName)
        {
            return tableName.Replace(".", "").ToLower();
        }
	}
}
