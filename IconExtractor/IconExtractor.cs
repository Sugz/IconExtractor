﻿/*
 *  IconExtractor/IconUtil for .NET
 *  Copyright (C) 2014 Tsuda Kageyu. All rights reserved.
 *
 *  Redistribution and use in source and binary forms, with or without
 *  modification, are permitted provided that the following conditions
 *  are met:
 *
 *   1. Redistributions of source code must retain the above copyright
 *      notice, this list of conditions and the following disclaimer.
 *   2. Redistributions in binary form must reproduce the above copyright
 *      notice, this list of conditions and the following disclaimer in the
 *      documentation and/or other materials provided with the distribution.
 *
 *  THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
 *  "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED
 *  TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A
 *  PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER
 *  OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
 *  EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
 *  PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
 *  PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
 *  LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
 *  NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 *  SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TsudaKageyu
{
    public class IconExtractor
    {
        ////////////////////////////////////////////////////////////////////////
        // Constants

        // Flags for LoadLibraryEx().

        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

        // Resource types for EnumResourceNames().

        private readonly static IntPtr RT_ICON = (IntPtr)3;
        private readonly static IntPtr RT_GROUP_ICON = (IntPtr)14;

        private const int MAX_PATH = 260;

        ////////////////////////////////////////////////////////////////////////
        // Fields

        private List<byte[]> iconData = null;   // Binary data of each icon. 

        ////////////////////////////////////////////////////////////////////////
        // Public properties

        /// <summary>
        /// Gets the full path of the associated file. 
        /// </summary>
        public string FileName
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the count of the icons in the associated file.
        /// </summary>
        public int Count
        {
            get { return iconData.Count; }
        }

        /// <summary>
        /// Initializes a new instance of the IconExtractor class from the specified file name.
        /// </summary>
        /// <param name="fileName">The file to extract icons from.</param>
        public IconExtractor(string fileName)
        {
            Initialize(fileName);
        }

        /// <summary>
        /// Extracts an icon from the file.
        /// </summary>
        /// <param name="index">Zero based index of the icon to be extracted.</param>
        /// <returns>A System.Drawing.Icon object.</returns>
        /// <remarks>Always returns new copy of the Icon. It should be disposed by the user.</remarks>
        public Icon GetIcon(int index)
        {
            if (index < 0 || Count <= index)
                throw new ArgumentOutOfRangeException("index");

            using (var ms = new MemoryStream(iconData[index]))
            {
                return new Icon(ms);
            }
        }

        /// <summary>
        /// Extracts all the icons from the file.
        /// </summary>
        /// <returns>An array of System.Drawing.Icon objects.</returns>
        /// <remarks>Always returns new copies of the Icons. They should be disposed by the user.</remarks>
        public Icon[] GetAllIcons()
        {
            var icons = new List<Icon>();
            for (int i = 0; i < Count; ++i)
                icons.Add(GetIcon(i));

            return icons.ToArray();
        }

        private void Initialize(string fileName)
        {
            if (fileName == null)
                throw new ArgumentNullException("fileName");

            var iconDirs = new List<byte[]>();
            var iconPics = new Dictionary<int, byte[]>();

            IntPtr hModule = IntPtr.Zero;
            try
            {
                // Collect resource data from the file.

                hModule = NativeMethods.LoadLibraryEx(fileName, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
                if (hModule == IntPtr.Zero)
                    throw new Win32Exception();

                ENUMRESNAMEPROC callback = (hMod, type, name, lparam) =>
                {
                    if (type == RT_GROUP_ICON)
                        iconDirs.Add(GetDataFromResource(hMod, type, name));
                    else if (type == RT_ICON)
                        iconPics.Add((ushort)name, GetDataFromResource(hMod, type, name));

                    return true;
                };
                NativeMethods.EnumResourceNames(hModule, RT_GROUP_ICON, callback, IntPtr.Zero);
                NativeMethods.EnumResourceNames(hModule, RT_ICON, callback, IntPtr.Zero);

                FileName = GetFileName(hModule);
            }
            finally
            {
                if (hModule != IntPtr.Zero)
                    NativeMethods.FreeLibrary(hModule);
            }

            // Build .ico files in memory out of the resource data. 

            iconData = new List<byte[]>();

            foreach (var dir in iconDirs)
            {
                using (var writer = new BinaryWriter(new MemoryStream()))
                {
                    // Refer the following URL for the data structures:
                    // http://msdn.microsoft.com/en-us/library/ms997538.aspx

                    // Copy GRPICONDIR to ICONDIR.

                    writer.Write(dir, 0, 6);

                    int count = BitConverter.ToUInt16(dir, 4);  // GRPICONDIR.idCount
                    int offset = 6 + 16 * count;                // sizeof(ICONDIR) + sizeof(ICONDIRENTRY) * count
                    var pics = new byte[count][];

                    for (int i = 0; i < count; ++i)
                    {
                        // Copy GRPICONDIRENTRY to ICONDIRENTRY.

                        writer.Write(dir, 6 + 14 * i, 12);  // First 12bytes are identical.
                        writer.Write(offset);               // Write offset instead of ID.

                        ushort id = BitConverter.ToUInt16(dir, 6 + 14 * i + 12);    // GRPICONDIRENTRY.nID
                        pics[i] = iconPics[id];

                        offset += pics[i].Length;
                    }

                    // Copy pictures.

                    for (int i = 0; i < count; ++i)
                        writer.Write(pics[i], 0, pics[i].Length);

                    iconData.Add(((MemoryStream)writer.BaseStream).ToArray());
                }
            }
        }

        private byte[] GetDataFromResource(IntPtr hModule, IntPtr type, IntPtr name)
        {
            IntPtr hResInfo = NativeMethods.FindResource(hModule, name, type);
            if (hResInfo == IntPtr.Zero)
                throw new Win32Exception();

            IntPtr hResData = NativeMethods.LoadResource(hModule, hResInfo);
            if (hResData == IntPtr.Zero)
                throw new Win32Exception();

            IntPtr pResData = NativeMethods.LockResource(hResData);
            if (pResData == IntPtr.Zero)
                throw new Win32Exception();

            uint size = NativeMethods.SizeofResource(hModule, hResInfo);
            if (size == 0)
                throw new Win32Exception();

            byte[] buf = new byte[size];
            Marshal.Copy(pResData, buf, 0, buf.Length);

            return buf;
        }

        private string GetFileName(IntPtr hModule)
        {
            // Get the file name in the format like:
            // "\\Device\\HarddiskVolume2\\Windows\\System32\\shell32.dll"

            string fileName;
            {
                var buf = new StringBuilder(MAX_PATH);
                int len = NativeMethods.GetMappedFileName(
                    NativeMethods.GetCurrentProcess(), hModule, buf, buf.Capacity);
                if (len == 0)
                    throw new Win32Exception();

                fileName = buf.ToString();
            }

            // Convert the device name to drive name like:
            // "C:\\Windows\\System32\\shell32.dll"

            for (char c = 'A'; c <= 'Z'; ++c)
            {
                var drive = c + ":";
                var buf = new StringBuilder(MAX_PATH);
                int len = NativeMethods.QueryDosDevice(drive, buf, buf.Capacity);
                if (len == 0)
                    continue;

                var devPath = buf.ToString();
                if (fileName.StartsWith(devPath))
                    return (drive + fileName.Substring(devPath.Length));
            }

            return fileName;
        }
    }
}
