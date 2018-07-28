﻿using OpenCensus.Tags.Propagation;
using System;
using System.Collections.Generic;
using System.Text;

namespace OpenCensus.Tags
{
    public class NoopTagContextBinarySerializer : TagContextBinarySerializerBase
    {
        internal static readonly ITagContextBinarySerializer INSTANCE = new NoopTagContextBinarySerializer();
        static readonly byte[] EMPTY_BYTE_ARRAY = { };
        public override byte[] ToByteArray(ITagContext tags)
        {
            if (tags == null)
            {
                throw new ArgumentNullException(nameof(tags));
            }
            return EMPTY_BYTE_ARRAY;
        }

        public override ITagContext FromByteArray(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }
            return NoopTags.NoopTagContext;
        }
    }
}
