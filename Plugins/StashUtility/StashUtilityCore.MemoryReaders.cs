// <copyright file="StashUtilityCore.MemoryReaders.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace StashUtility
{
    using System;
    using GameOffsets.Natives;

    public sealed partial class StashUtilityCore
    {
        // Bind once per process handle to avoid reflection argument arrays and boxed structs per read.
        private delegate T MemoryReader<T>(IntPtr address) where T : unmanaged;
        private delegate bool MemoryTryReader<T>(IntPtr address, out T value) where T : unmanaged;
        private delegate T[] VectorReader<T>(StdVector vector) where T : unmanaged;

        private void ClearEquipmentMemoryDelegates()
        {
            readMemoryMethods.Clear();
            tryReadMemoryMethods.Clear();
            readStdVectorMethods.Clear();
        }
    }
}
