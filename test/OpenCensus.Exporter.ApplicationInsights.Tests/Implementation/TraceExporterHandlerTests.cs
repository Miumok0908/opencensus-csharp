﻿// <copyright file="TraceExporterHandlerTests.cs" company="OpenCensus Authors">
// Copyright 2018, OpenCensus Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of theLicense at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// </copyright>

namespace OpenCensus.Exporter.ApplicationInsights.Tests
{
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.ApplicationInsights.Extensibility.Implementation;
    using OpenCensus.Common;
    using OpenCensus.Exporter.ApplicationInsights.Implementation;
    using OpenCensus.Trace;
    using OpenCensus.Trace.Export;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    using Xunit;

    public class OpenCensusTelemetryConverterTests
    {
        private const string TestTraceId = "d79bdda7eb9c4a9fa9bda52fe7b48b95";
        private const string TestSpanId = "d7ddeb4aa9a5e78b";
        private const string TestParentSpanId = "9ba79c9fbd2fb495";

        private readonly byte[] testTraceIdBytes = { 0xd7, 0x9b, 0xdd, 0xa7, 0xeb, 0x9c, 0x4a, 0x9f, 0xa9, 0xbd, 0xa5, 0x2f, 0xe7, 0xb4, 0x8b, 0x95 };
        private readonly byte[] testSpanIdBytes = { 0xd7, 0xdd, 0xeb, 0x4a, 0xa9, 0xa5, 0xe7, 0x8b };
        private readonly byte[] testParentSpanIdBytes = { 0x9b, 0xa7, 0x9c, 0x9f, 0xbd, 0x2f, 0xb4, 0x95 };

        public class TestClock : IClock
        {
            private readonly DateTimeOffset nowSecondsPrecision;
            private int NanosecondsAfterSeconds;

            public TestClock(DateTimeOffset now)
            {
                // remove ticks
                this.nowSecondsPrecision = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds());
                this.NanosecondsAfterSeconds = (int)(now.Subtract(this.nowSecondsPrecision).Ticks * 100 + 23); //23 is just a random constant
            }

            public TestClock GetBefore(TimeSpan span)
            {
                return new TestClock(this.nowSecondsPrecision.Subtract(span).AddTicks(this.NanosecondsAfterSeconds / 100));
            }

            public Timestamp Now
            {
                get
                {
                    return Timestamp.Create(this.nowSecondsPrecision.ToUnixTimeSeconds(), NanosecondsAfterSeconds);
                }
            }

            public long NowNanos
            {
                get
                {
                    return nowSecondsPrecision.ToUnixTimeSeconds() * 1000 * 1000 * 1000 + NanosecondsAfterSeconds;
                }
            }
        }

        private ConcurrentQueue<ITelemetry> ConvertSpan(ISpanData data)
        {
            var sentItems = new ConcurrentQueue<ITelemetry>();
            var configuration = new TelemetryConfiguration();
            ITelemetryChannel channel = new StubTelemetryChannel
            {
                OnSend = t => sentItems.Enqueue(t),
            };

            configuration.TelemetryChannel = channel;

            TraceExporterHandler exporter = new TraceExporterHandler(configuration);
            exporter.Export(new List<ISpanData> { data });

            return sentItems;
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequest()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            Assert.Single(sentItems);
            Assert.True(sentItems.Single() is RequestTelemetry);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal("spanName", request.Name);
            Assert.Equal(now.GetBefore(TimeSpan.FromSeconds(1)).NowNanos, new TestClock(request.Timestamp).NowNanos);
            Assert.Equal(1, request.Duration.TotalSeconds);

            Assert.Equal(TestTraceId, request.Context.Operation.Id);
            Assert.Null(request.Context.Operation.ParentId);

            Assert.Equal($"|{TestTraceId}.{TestSpanId}.", request.Id);

            Assert.False(request.Success.HasValue);
            Assert.True(string.IsNullOrEmpty(request.ResponseCode));

            // TODO: implement this
            //Assert.Equal("lf_unspecified-oc:0.0.0", request.Context.GetInternalContext().SdkVersion);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithTracestate()
        {
            // ARRANGE

            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);

            context = SpanContext.Create(
                context.TraceId,
                context.SpanId,
                context.TraceOptions,
                tracestate: context.Tracestate.ToBuilder()
                    .Set("k1", "v1")
                    .Set("k2", "v2")
                    .Build());

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            Assert.Single(sentItems);
            Assert.True(sentItems.Single() is RequestTelemetry);

            var request = sentItems.OfType<RequestTelemetry>().Single();

            Assert.Equal(2, request.Properties.Count);
            Assert.Equal("v1", request.Properties["k1"]);
            Assert.Equal("v2", request.Properties["k2"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithParent()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);

            parentSpanId = SpanId.FromBytes(this.testParentSpanIdBytes);

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            Assert.Equal($"|{TestTraceId}.{TestParentSpanId}.", ((RequestTelemetry)sentItems.Single()).Context.Operation.ParentId);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithInvalidParent()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);

            parentSpanId = SpanId.Invalid;

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            Assert.Null(((RequestTelemetry)sentItems.Single()).Context.Operation.ParentId);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithStatus()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);

            status = Status.Ok;

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            var request = (RequestTelemetry)sentItems.Single();

            Assert.True(request.Success.HasValue);
            Assert.True(request.Success.Value);
            Assert.Equal("0", request.ResponseCode); // this check doesn't match Local Forwarder Assert.IsTrue(string.IsNullOrEmpty(request.ResponseCode));
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithStatusAndDescription()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);

            status = Status.Ok.WithDescription("all good");

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            var request = (RequestTelemetry)sentItems.Single();

            Assert.True(request.Success.HasValue);
            Assert.True(request.Success.Value);
            Assert.Equal("0", request.ResponseCode);  // this check doesn't match Local Forwarder Assert.AreEqual("all good", request.ResponseCode);
            Assert.Equal("all good", request.Properties["statusDescription"]);  // this check doesn't match Local Forwarder
        }
        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithNonSuccessStatusAndDescription()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);

            status = Status.Cancelled.WithDescription("all bad");

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);


            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            var request = (RequestTelemetry)sentItems.Single();

            Assert.True(request.Success.HasValue);
            Assert.False(request.Success.Value);
            Assert.Equal("1", request.ResponseCode);  // this check doesn't match Local Forwarder Assert.AreEqual("all bad", request.ResponseCode);
            Assert.Equal("all bad", request.Properties["statusDescription"]);  // this check doesn't match Local Forwarder
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestErrorAttribute()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);

            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>() { { "error", AttributeValue.BooleanAttributeValue(true) } }, 0);

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.True(request.Success.HasValue);
            Assert.False(request.Success.Value);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependency()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);

            kind = SpanKind.Client;

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            Assert.Single(sentItems);
            Assert.True(sentItems.Single() is DependencyTelemetry);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal("spanName", dependency.Name);
            Assert.Equal(now.GetBefore(TimeSpan.FromSeconds(1)).NowNanos, new TestClock(dependency.Timestamp).NowNanos);
            Assert.Equal(1, dependency.Duration.TotalSeconds);

            Assert.Equal(TestTraceId, dependency.Context.Operation.Id);
            Assert.Null(dependency.Context.Operation.ParentId);
            Assert.Equal($"|{TestTraceId}.{TestSpanId}.", dependency.Id);

            Assert.True(string.IsNullOrEmpty(dependency.ResultCode));
            Assert.False(dependency.Success.HasValue);

            // Assert.Equal("lf_unspecified-oc:0.0.0", dependency.Context.GetInternalContext().SdkVersion);

            Assert.True(string.IsNullOrEmpty(dependency.Type));
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithTracestate()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);

            kind = SpanKind.Client;
            context = SpanContext.Create(
                context.TraceId,
                context.SpanId,
                context.TraceOptions,
                tracestate: context.Tracestate.ToBuilder()
                    .Set("k1", "v1")
                    .Set("k2", "v2")
                    .Build());


            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            Assert.Single(sentItems);
            Assert.True(sentItems.Single() is DependencyTelemetry);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();

            Assert.Equal(2, dependency.Properties.Count);
            Assert.Equal("v1", dependency.Properties["k1"]);
            Assert.Equal("v2", dependency.Properties["k2"]);
            // Assert.Equal("lf_unspecified-oc:0.0.0", dependency.Context.GetInternalContext().SdkVersion);
        }

        /*
        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithAnnotationsAndNode()
        {
            // ARRANGE
            var now = DateTime.UtcNow;

            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);

            kind = SpanKind.Client;

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);


            span.TimeEvents = new Span.Types.TimeEvents
            {
                TimeEvent =
                        {
                            new Span.Types.TimeEvent
                            {
                                Time = now.ToTimestamp(),
                                Annotation = new Span.Types.TimeEvent.Types.Annotation
                                {
                                    Description = new TruncatableString {Value = "test message1"},
                                },
                            },
                            new Span.Types.TimeEvent
                            {
                                Time = now.ToTimestamp(),
                                MessageEvent = new Span.Types.TimeEvent.Types.MessageEvent
                                {
                                    Id = 1,
                                    CompressedSize = 2,
                                    UncompressedSize = 3,
                                    Type = Span.Types.TimeEvent.Types.MessageEvent.Types.Type.Received,
                                },
                            },
                        },
            };


            string hostName = "host", serviceName = "tests", version = "1.2.3.4.5";
            uint pid = 12345;
            var lang = LibraryInfo.Types.Language.CSharp;

            var node = CreateBasicNode(hostName, pid, lang, version, serviceName);
            node.Attributes.Add("a", "b");

            // ACT
            var sentItems = this.ConvertSpan(span, node, string.Empty);

            // ASSERT
            Assert.Equal(3, sentItems.Count);
            Assert.Single(sentItems.OfType<DependencyTelemetry>());
            Assert.Equal(2, sentItems.OfType<TraceTelemetry>().Count());

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            var trace1 = sentItems.OfType<TraceTelemetry>().First();
            var trace2 = sentItems.OfType<TraceTelemetry>().Last();
            Assert.Equal(serviceName, dependency.Context.Cloud.RoleName);
            Assert.Equal(serviceName, trace1.Context.Cloud.RoleName);
            Assert.Equal(serviceName, trace2.Context.Cloud.RoleName);

            Assert.Equal($"{hostName}.{pid}", dependency.Context.Cloud.RoleInstance);
            Assert.Equal($"{hostName}.{pid}", trace1.Context.Cloud.RoleInstance);
            Assert.Equal($"{hostName}.{pid}", trace2.Context.Cloud.RoleInstance);

            Assert.Equal(0, dependency.Properties.Count);
            Assert.Equal(0, trace1.Properties.Count);
            Assert.Equal(0, trace2.Properties.Count);
        }
        */

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithParent()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Client;
            parentSpanId = SpanId.FromBytes(this.testParentSpanIdBytes);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal($"|{TestTraceId}.{TestParentSpanId}.", dependency.Context.Operation.ParentId);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithStatus()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Client;
            status = Status.Ok;
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            var dependency = (DependencyTelemetry)sentItems.Single();

            Assert.True(dependency.Success.HasValue);
            Assert.True(dependency.Success.Value);
            Assert.Equal("0", dependency.ResultCode);
            Assert.False(dependency.Properties.ContainsKey("StatusDescription"));  // TODO: why it is upper case first letter?
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithStatusAndDescription()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Client;
            status = Status.Ok.WithDescription("all good");
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            var dependency = (DependencyTelemetry)sentItems.Single();

            Assert.True(dependency.Success.HasValue);
            Assert.True(dependency.Success.Value);

            Assert.Equal("0", dependency.ResultCode);
            Assert.True(dependency.Properties.ContainsKey("statusDescription"));
            Assert.Equal("all good", dependency.Properties["statusDescription"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithNonSuccessStatusAndDescription()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Client;
            status = Status.Cancelled.WithDescription("all bad");
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            var dependency = (DependencyTelemetry)sentItems.Single();

            Assert.True(dependency.Success.HasValue);
            Assert.False(dependency.Success.Value);
            Assert.Equal("1", dependency.ResultCode);
            Assert.True(dependency.Properties.ContainsKey("statusDescription"));
            Assert.Equal("all bad", dependency.Properties["statusDescription"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyErrorAttribute()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>() { { "error", AttributeValue.BooleanAttributeValue(true) } }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.True(dependency.Success.HasValue);
            Assert.False(dependency.Success.Value);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestBasedOnSpanKindAttribute()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>() { { "span.kind", AttributeValue.StringAttributeValue("server") } }, 0);

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            Assert.True(sentItems.Single() is RequestTelemetry);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestBasedOnSpanKindProperty()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            parentSpanId = SpanId.FromBytes(this.testParentSpanIdBytes);
            hasRemoteParent = null;
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            Assert.True(sentItems.Single() is RequestTelemetry);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyBasedOnSpanKindProperty()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Client;
            parentSpanId = SpanId.FromBytes(this.testParentSpanIdBytes);
            hasRemoteParent = null;
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            Assert.True(sentItems.Single() is DependencyTelemetry);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependenciesBasedOnSpanKindAttribute()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Unspecified;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>() { { "span.kind", AttributeValue.StringAttributeValue("client") } }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            Assert.True(sentItems.Single() is DependencyTelemetry);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestBasedOnSameProcessAsParentFlag()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Unspecified;
            hasRemoteParent = true;
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);
            var sentItems = this.ConvertSpan(span);

            Assert.True(sentItems.Single() is RequestTelemetry);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDepednencyBasedOnSameProcessAsParentFlag()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Unspecified;
            hasRemoteParent = false;
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);
            var sentItems = this.ConvertSpan(span);

            Assert.True(sentItems.Single() is DependencyTelemetry);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDepednencyBasedOnSameProcessAsParentFlagNotSet()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Unspecified;
            hasRemoteParent = null;
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            Assert.True(sentItems.Single() is DependencyTelemetry);
        }

        // TODO: should we allow null dates? There is no reason to not allow it
        //[Fact]
        //public void OpenCensusTelemetryConverterTests_TracksRequestWithoutName()
        //{
        //    this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
        //    name = null;
        //    var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

        //    var sentItems = this.ConvertSpan(span);

        //    Assert.Null(sentItems.OfType<RequestTelemetry>().Single().Name);
        //}

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithoutKind()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            kind = SpanKind.Unspecified;
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            Assert.True(sentItems.Single() is DependencyTelemetry);
        }

        // TODO: should we allow null dates? There is no reason to not allow it
        //[Fact]
        //public void OpenCensusTelemetryConverterTests_TracksRequestWithoutStartAndEndTime()
        //{
        //    this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
        //    startTimestamp = null;
        //    endTimestamp = null;
        //    var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

        //    var sentItems = this.ConvertSpan(span);

        //    var request = sentItems.OfType<RequestTelemetry>().Single();
        //    Assert.True(Math.Abs((request.Timestamp - DateTime.UtcNow).TotalSeconds) < 1);
        //    Assert.Equal(0, request.Duration.TotalSeconds);
        //}

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestWithUrl()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.url", AttributeValue.StringAttributeValue(url.ToString()) },
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.status_code", AttributeValue.LongAttributeValue(409) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal(url.ToString(), request.Url.ToString());
            Assert.Equal("POST /path", request.Name);
            Assert.Equal("409", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestWithRelativeUrl()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.url", AttributeValue.StringAttributeValue(url.LocalPath) },
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.status_code", AttributeValue.LongAttributeValue(409) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal("/path", request.Url.ToString()); // This check doesn't match Local Forwarder Assert.Null(request.Url);
            Assert.Equal("POST /path", request.Name);
            Assert.Equal("409", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestWithUrlAndRoute()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.url", AttributeValue.StringAttributeValue(url.ToString()) },
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.route", AttributeValue.StringAttributeValue("route") },
                    { "http.status_code", AttributeValue.LongAttributeValue(503) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal(url.ToString(), request.Url.ToString());
            Assert.Equal("POST route", request.Name);
            Assert.Equal("503", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestWithUrlAndNoMethod()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.url", AttributeValue.StringAttributeValue(url.ToString()) },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal(url.ToString(), request.Url.ToString());
            Assert.Equal("/path", request.Name);
            Assert.Equal("200", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestWithUrlOtherAttributesAreIgnored()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.url", AttributeValue.StringAttributeValue(url.ToString()) },
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.path", AttributeValue.StringAttributeValue("another path") },
                    { "http.host", AttributeValue.StringAttributeValue("another host") },
                    { "http.port", AttributeValue.LongAttributeValue(8080) },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal(url.ToString(), request.Url.ToString());
            Assert.Equal("POST another path", request.Name); // This check doesn't match Local Forwarder Assert.AreEqual("POST /path", request.Name);
            Assert.Equal("200", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithStringStatusCode()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.status_code", AttributeValue.LongAttributeValue(201) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            var request = (RequestTelemetry)sentItems.Single();

            Assert.Equal("201", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestHostPortPathAttributes()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.path", AttributeValue.StringAttributeValue("path") },
                    { "http.host", AttributeValue.StringAttributeValue("host") },
                    { "http.port", AttributeValue.LongAttributeValue(123) },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal("https://host:123/path", request.Url.ToString());
            Assert.Equal("POST path", request.Name);
            Assert.Equal("200", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestPortPathAndEmptyHostAttributes()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.path", AttributeValue.StringAttributeValue("path") },
                    { "http.host", AttributeValue.StringAttributeValue("") },
                    { "http.port", AttributeValue.LongAttributeValue(123) },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal("/path", request.Url.ToString());  // This check doesn't match Local Forwarder Assert.IsNull(request.Url);
            Assert.Equal("POST path", request.Name);
            Assert.Equal("200", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestHostPathAttributes()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.path", AttributeValue.StringAttributeValue("/path") },
                    { "http.host", AttributeValue.StringAttributeValue("host") },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal("https://host/path", request.Url.ToString());
            Assert.Equal("POST /path", request.Name);
            Assert.Equal("200", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestHostAttributes()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.host", AttributeValue.StringAttributeValue("host") },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal("https://host/", request.Url.ToString());
            Assert.Equal("POST", request.Name);
            Assert.Equal("200", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestOnlyMethodAttributes()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Null(request.Url);
            Assert.Equal("POST", request.Name);
            Assert.Equal("200", request.ResponseCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithStringStatusCode()
        {
            // ARRANGE
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            kind = SpanKind.Client;
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.status_code", AttributeValue.LongAttributeValue(201) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            // ACT
            var sentItems = this.ConvertSpan(span);

            // ASSERT
            var dependency = (DependencyTelemetry)sentItems.Single();

            Assert.Equal("201", dependency.ResultCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpRequestUserAgent()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host/path");
            var userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/67.0.3396.99 Safari/537.36";
            name = "HttpIn";
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.url", AttributeValue.StringAttributeValue(url.ToString()) },
                    { "http.user_agent", AttributeValue.StringAttributeValue(userAgent) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal(userAgent, request.Context.User.UserAgent);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpDependencyWithUrl()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpOut";
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.url", AttributeValue.StringAttributeValue(url.ToString()) },
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal(url.ToString(), dependency.Data);
            Assert.Equal("POST /path", dependency.Name);
            Assert.Equal("200", dependency.ResultCode);
            Assert.Equal("host", dependency.Target);
            Assert.Equal("Http", dependency.Type);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpDependencyWithRelativeUrl()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpOut";
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.url", AttributeValue.StringAttributeValue(url.LocalPath) },
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal(url.LocalPath, dependency.Data);
            Assert.Equal("POST /path", dependency.Name);
            Assert.Equal("200", dependency.ResultCode);
            Assert.Null(dependency.Target);
            Assert.Equal("Http", dependency.Type);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpDependencyWithUrlIgnoresHostPortPath()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpOut";
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.url", AttributeValue.StringAttributeValue(url.ToString()) },
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.path", AttributeValue.StringAttributeValue("another path") },
                    { "http.host", AttributeValue.StringAttributeValue("another host") },
                    { "http.port", AttributeValue.LongAttributeValue(8080) },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal(url.ToString(), dependency.Data);
            Assert.Equal("POST another path", dependency.Name);  // This check doesn't match Local Forwarder Assert.AreEqual("POST /path", dependency.Name);
            Assert.Equal("200", dependency.ResultCode);
            Assert.Equal("host", dependency.Target);
            Assert.Equal("Http", dependency.Type);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpDependencyWithHostPortPath()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            name = "HttpOut";
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.path", AttributeValue.StringAttributeValue("/path") },
                    { "http.host", AttributeValue.StringAttributeValue("host") },
                    { "http.port", AttributeValue.LongAttributeValue(123) },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal("https://host:123/path", dependency.Data);
            Assert.Equal("POST /path", dependency.Name);
            Assert.Equal("200", dependency.ResultCode);
            Assert.Equal("host", dependency.Target);
            Assert.Equal("Http", dependency.Type);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpDependencyWithHostPort()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            name = "HttpOut";
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.host", AttributeValue.StringAttributeValue("host") },
                    { "http.port", AttributeValue.LongAttributeValue(123) },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal("https://host:123/", dependency.Data);
            Assert.Equal("POST", dependency.Name);
            Assert.Equal("200", dependency.ResultCode);
            Assert.Equal("host", dependency.Target);
            Assert.Equal("Http", dependency.Type);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpDependencyWithPathAndEmptyHost()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            name = "HttpOut";
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.path", AttributeValue.StringAttributeValue("/path") },
                    { "http.host", AttributeValue.StringAttributeValue("") },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal("/path", dependency.Data);  // This check doesn't match Local Forwarder Assert.IsNull(dependency.Data);
            Assert.Equal("POST /path", dependency.Name);
            Assert.Equal("200", dependency.ResultCode);
            Assert.True(string.IsNullOrEmpty(dependency.Target));
            Assert.Equal("Http", dependency.Type);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpDependencyWithHost()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpOut";
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.host", AttributeValue.StringAttributeValue("host") },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal("https://host/", dependency.Data);
            Assert.Equal("POST", dependency.Name);
            Assert.Equal("200", dependency.ResultCode);
            Assert.Equal("host", dependency.Target);
            Assert.Equal("Http", dependency.Type);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpDependencyWithMethod()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpOut";
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.method", AttributeValue.StringAttributeValue("POST") },
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            
            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Null(dependency.Data);
            Assert.Equal("POST", dependency.Name);
            Assert.Equal("200", dependency.ResultCode);
            Assert.Null(dependency.Target);
            Assert.Equal("Http", dependency.Type);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHttpDependencyWithStatusCodeOnly()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "HttpOut";
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "http.status_code", AttributeValue.LongAttributeValue(200) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);
            
            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Null(dependency.Data);
            Assert.Equal("HttpOut", dependency.Name);  // This check doesn't match Local Forwarder
            Assert.Null(dependency.Target);
            Assert.Equal("Http", dependency.Type);
            Assert.Equal("200", dependency.ResultCode);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithCustomAttributes()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "spanName";
            kind = SpanKind.Client;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "custom.stringAttribute", AttributeValue.StringAttributeValue("string") },
                    { "custom.longAttribute", AttributeValue.LongAttributeValue(long.MaxValue) },
                    { "custom.boolAttribute", AttributeValue.BooleanAttributeValue(true) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal("spanName", dependency.Name);
            Assert.Equal(3, dependency.Properties.Count);
            Assert.True(dependency.Properties.ContainsKey("custom.stringAttribute"));
            Assert.Equal("string", dependency.Properties["custom.stringAttribute"]);

            Assert.True(dependency.Properties.ContainsKey("custom.longAttribute"));
            Assert.Equal(long.MaxValue.ToString(), dependency.Properties["custom.longAttribute"]);

            Assert.True(dependency.Properties.ContainsKey("custom.boolAttribute"));
            Assert.Equal(bool.TrueString, dependency.Properties["custom.boolAttribute"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestsWithCustomAttributes()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            var url = new Uri("https://host:123/path?query");
            name = "spanName";
            kind = SpanKind.Server;
            attributes = Attributes.Create(new Dictionary<string, IAttributeValue>()
                {
                    { "custom.stringAttribute", AttributeValue.StringAttributeValue("string") },
                    { "custom.longAttribute", AttributeValue.LongAttributeValue(long.MaxValue) },
                    { "custom.boolAttribute", AttributeValue.BooleanAttributeValue(true) },
                }, 0);
            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal("spanName", request.Name);
            Assert.Equal(3, request.Properties.Count);
            Assert.True(request.Properties.ContainsKey("custom.stringAttribute"));
            Assert.Equal("string", request.Properties["custom.stringAttribute"]);

            Assert.True(request.Properties.ContainsKey("custom.longAttribute"));
            Assert.Equal(long.MaxValue.ToString(), request.Properties["custom.longAttribute"]);

            Assert.True(request.Properties.ContainsKey("custom.boolAttribute"));
            Assert.Equal(bool.TrueString, request.Properties["custom.boolAttribute"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithLinks()
        {
            var (link0TraceId, link0TraceIdBytes) = GenerateRandomId(16);
            var (link1TraceId, link1TraceIdBytes) = GenerateRandomId(16);
            var (link2TraceId, link2TraceIdBytes) = GenerateRandomId(16);

            var (link0SpanId, link0SpanIdBytes) = GenerateRandomId(8);
            var (link1SpanId, link1SpanIdBytes) = GenerateRandomId(8);
            var (link2SpanId, link2SpanIdBytes) = GenerateRandomId(8);

            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            name = "spanName";
            kind = SpanKind.Client;

            links = LinkList.Create(new List<ILink>() {
                    Link.FromSpanContext(
                        SpanContext.Create(TraceId.FromBytes(link0TraceIdBytes), SpanId.FromBytes(link0SpanIdBytes), TraceOptions.Default, Tracestate.Empty), LinkType.ChildLinkedSpan),
                    Link.FromSpanContext(
                        SpanContext.Create(TraceId.FromBytes(link1TraceIdBytes), SpanId.FromBytes(link1SpanIdBytes), TraceOptions.Default, Tracestate.Empty), LinkType.ParentLinkedSpan),
                    Link.FromSpanContext(
                        SpanContext.Create(TraceId.FromBytes(link2TraceIdBytes), SpanId.FromBytes(link2SpanIdBytes), TraceOptions.Default, Tracestate.Empty), LinkType.Unspecified),
                    }, 0);

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal(9, dependency.Properties.Count);

            Assert.True(dependency.Properties.ContainsKey("link0_traceId"));
            Assert.True(dependency.Properties.ContainsKey("link1_traceId"));
            Assert.True(dependency.Properties.ContainsKey("link2_traceId"));

            Assert.Equal(link0TraceId, dependency.Properties["link0_traceId"]);
            Assert.Equal(link1TraceId, dependency.Properties["link1_traceId"]);
            Assert.Equal(link2TraceId, dependency.Properties["link2_traceId"]);

            Assert.True(dependency.Properties.ContainsKey("link0_spanId"));
            Assert.True(dependency.Properties.ContainsKey("link1_spanId"));
            Assert.True(dependency.Properties.ContainsKey("link2_spanId"));

            Assert.Equal(link0SpanId, dependency.Properties["link0_spanId"]);
            Assert.Equal(link1SpanId, dependency.Properties["link1_spanId"]);
            Assert.Equal(link2SpanId, dependency.Properties["link2_spanId"]);

            Assert.True(dependency.Properties.ContainsKey("link0_type"));
            Assert.True(dependency.Properties.ContainsKey("link1_type"));
            Assert.True(dependency.Properties.ContainsKey("link2_type"));

            Assert.Equal("ChildLinkedSpan", dependency.Properties["link0_type"]);
            Assert.Equal("ParentLinkedSpan", dependency.Properties["link1_type"]);
            Assert.Equal("Unspecified", dependency.Properties["link2_type"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithLinksAndAttributes()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            name = "spanName";
            kind = SpanKind.Client;

            links = LinkList.Create(
                new List<ILink>() {
                    Link.FromSpanContext(
                        SpanContext.Create(
                            TraceId.FromBytes(GenerateRandomId(16).Item2),
                            SpanId.FromBytes(GenerateRandomId(8).Item2),
                            TraceOptions.Default,
                            Tracestate.Empty),
                        LinkType.ChildLinkedSpan,
                        new Dictionary<string, IAttributeValue>()
                        {
                            { "some.str.attribute", AttributeValue.StringAttributeValue("foo") },
                            { "some.int.attribute", AttributeValue.LongAttributeValue(1) },
                            { "some.bool.attribute", AttributeValue.BooleanAttributeValue(true) },
                        }),
                },
                droppedLinksCount: 0);

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal(6, dependency.Properties.Count);

            Assert.True(dependency.Properties.ContainsKey("link0_some.str.attribute"));
            Assert.Equal("foo", dependency.Properties["link0_some.str.attribute"]);

            Assert.True(dependency.Properties.ContainsKey("link0_some.int.attribute"));
            Assert.Equal("1", dependency.Properties["link0_some.int.attribute"]);

            Assert.True(dependency.Properties.ContainsKey("link0_some.bool.attribute"));
            Assert.Equal(bool.TrueString, dependency.Properties["link0_some.bool.attribute"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithLinks()
        {
            var (link0TraceId, link0TraceIdBytes) = GenerateRandomId(16);
            var (link1TraceId, link1TraceIdBytes) = GenerateRandomId(16);
            var (link2TraceId, link2TraceIdBytes) = GenerateRandomId(16);

            var (link0SpanId, link0SpanIdBytes) = GenerateRandomId(8);
            var (link1SpanId, link1SpanIdBytes) = GenerateRandomId(8);
            var (link2SpanId, link2SpanIdBytes) = GenerateRandomId(8);

            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            name = "spanName";
            kind = SpanKind.Server;

            links = LinkList.Create(new List<ILink>() {
                    Link.FromSpanContext(
                        SpanContext.Create(TraceId.FromBytes(link0TraceIdBytes), SpanId.FromBytes(link0SpanIdBytes), TraceOptions.Default, Tracestate.Empty), LinkType.ChildLinkedSpan),
                    Link.FromSpanContext(
                        SpanContext.Create(TraceId.FromBytes(link1TraceIdBytes), SpanId.FromBytes(link1SpanIdBytes), TraceOptions.Default, Tracestate.Empty), LinkType.ParentLinkedSpan),
                    Link.FromSpanContext(
                        SpanContext.Create(TraceId.FromBytes(link2TraceIdBytes), SpanId.FromBytes(link2SpanIdBytes), TraceOptions.Default, Tracestate.Empty), LinkType.Unspecified),
            }, 0);

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal(9, request.Properties.Count);

            Assert.True(request.Properties.ContainsKey("link0_traceId"));
            Assert.True(request.Properties.ContainsKey("link1_traceId"));
            Assert.True(request.Properties.ContainsKey("link2_traceId"));

            Assert.Equal(link0TraceId, request.Properties["link0_traceId"]);
            Assert.Equal(link1TraceId, request.Properties["link1_traceId"]);
            Assert.Equal(link2TraceId, request.Properties["link2_traceId"]);

            Assert.True(request.Properties.ContainsKey("link0_spanId"));
            Assert.True(request.Properties.ContainsKey("link1_spanId"));
            Assert.True(request.Properties.ContainsKey("link2_spanId"));

            Assert.Equal(link0SpanId, request.Properties["link0_spanId"]);
            Assert.Equal(link1SpanId, request.Properties["link1_spanId"]);
            Assert.Equal(link2SpanId, request.Properties["link2_spanId"]);

            Assert.True(request.Properties.ContainsKey("link0_type"));
            Assert.True(request.Properties.ContainsKey("link1_type"));
            Assert.True(request.Properties.ContainsKey("link2_type"));

            Assert.Equal("ChildLinkedSpan", request.Properties["link0_type"]);
            Assert.Equal("ParentLinkedSpan", request.Properties["link1_type"]);
            Assert.Equal("Unspecified", request.Properties["link2_type"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithLinksAndAttributes()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            name = "spanName";
            kind = SpanKind.Server;

            links = LinkList.Create(
                new List<ILink>() {
                    Link.FromSpanContext(
                        SpanContext.Create(
                            TraceId.FromBytes(GenerateRandomId(16).Item2),
                            SpanId.FromBytes(GenerateRandomId(8).Item2),
                            TraceOptions.Default,
                            Tracestate.Empty),
                        LinkType.ChildLinkedSpan,
                        new Dictionary<string, IAttributeValue>()
                        {
                            { "some.str.attribute", AttributeValue.StringAttributeValue("foo") },
                            { "some.int.attribute", AttributeValue.LongAttributeValue(1) },
                            { "some.bool.attribute", AttributeValue.BooleanAttributeValue(true) },
                        }),
                },
                droppedLinksCount: 0);

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal(6, request.Properties.Count);

            Assert.True(request.Properties.ContainsKey("link0_some.str.attribute"));
            Assert.Equal("foo", request.Properties["link0_some.str.attribute"]);

            Assert.True(request.Properties.ContainsKey("link0_some.int.attribute"));
            Assert.Equal("1", request.Properties["link0_some.int.attribute"]);

            Assert.True(request.Properties.ContainsKey("link0_some.bool.attribute"));
            Assert.Equal(bool.TrueString, request.Properties["link0_some.bool.attribute"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithAnnotations()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            Thread.Sleep(TimeSpan.FromTicks(10));
            name = "spanName";
            kind = SpanKind.Server;

            annotations = TimedEvents<IAnnotation>.Create(
                new List<ITimedEvent<IAnnotation>>() {
                    TimedEvent<IAnnotation>.Create(now.Now, Annotation.FromDescription("test message1")),
                    TimedEvent<IAnnotation>.Create(null, Annotation.FromDescriptionAndAttributes("test message2", new Dictionary<string, IAttributeValue>()
                        {
                            { "custom.stringAttribute", AttributeValue.StringAttributeValue("string") },
                            { "custom.longAttribute", AttributeValue.LongAttributeValue(long.MaxValue) },
                            { "custom.boolAttribute", AttributeValue.BooleanAttributeValue(true) },
                        })),
                },
                droppedEventsCount: 0);

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            Assert.Equal(3, sentItems.Count);
            Assert.Single(sentItems.OfType<RequestTelemetry>());
            Assert.Equal(2, sentItems.OfType<TraceTelemetry>().Count());

            var request = sentItems.OfType<RequestTelemetry>().Single();
            var trace1 = sentItems.OfType<TraceTelemetry>().First();
            var trace2 = sentItems.OfType<TraceTelemetry>().Last();

            Assert.Equal(request.Context.Operation.Id, trace1.Context.Operation.Id);
            Assert.Equal(request.Context.Operation.Id, trace2.Context.Operation.Id);
            Assert.Equal(request.Id, trace1.Context.Operation.ParentId);
            Assert.Equal(request.Id, trace2.Context.Operation.ParentId);

            Assert.Equal("test message1", trace1.Message);
            Assert.Equal("test message2", trace2.Message);

            Assert.Equal(now.NowNanos, new TestClock(trace1.Timestamp).NowNanos);
            Assert.NotEqual(now.NowNanos, new TestClock(trace2.Timestamp).NowNanos);
            Assert.True(Math.Abs((DateTime.UtcNow - trace2.Timestamp).TotalSeconds) < 1);

            Assert.False(trace1.Properties.Any());
            Assert.Equal(3, trace2.Properties.Count);
            Assert.True(trace2.Properties.ContainsKey("custom.stringAttribute"));
            Assert.Equal("string", trace2.Properties["custom.stringAttribute"]);

            Assert.True(trace2.Properties.ContainsKey("custom.longAttribute"));
            Assert.Equal(long.MaxValue.ToString(), trace2.Properties["custom.longAttribute"]);

            Assert.True(trace2.Properties.ContainsKey("custom.boolAttribute"));
            Assert.Equal(bool.TrueString, trace2.Properties["custom.boolAttribute"]);
        }

        /*
        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithAnnotationsAndNode()
        {
            // ARRANGE
            var now = DateTime.UtcNow;

            var span = this.CreateBasicSpan(SpanKind.Server, "spanName");
            span.TimeEvents = new Span.Types.TimeEvents
            {
                TimeEvent =
                {
                    new Span.Types.TimeEvent
                    {
                        Time = now.ToTimestamp(),
                        Annotation = new Span.Types.TimeEvent.Types.Annotation
                        {
                            Description = new TruncatableString {Value = "test message1"},
                        },
                    },
                    new Span.Types.TimeEvent
                    {
                        Time = now.ToTimestamp(),
                        MessageEvent = new Span.Types.TimeEvent.Types.MessageEvent
                        {
                            Id = 1,
                            CompressedSize = 2,
                            UncompressedSize = 3,
                            Type = Span.Types.TimeEvent.Types.MessageEvent.Types.Type.Received,
                        },
                    },
                },
            };


            string hostName = "host", serviceName = "tests", version = "1.2.3.4.5";
            uint pid = 12345;
            var lang = LibraryInfo.Types.Language.CSharp;

            var node = CreateBasicNode(hostName, pid, lang, version, serviceName);
            node.Attributes.Add("a", "b");

            // ACT
            var sentItems = this.ConvertSpan(span, node, string.Empty);

            // ASSERT
            Assert.Equal(3, sentItems.Count);
            Assert.Single(sentItems.OfType<RequestTelemetry>());
            Assert.Equal(2, sentItems.OfType<TraceTelemetry>().Count());

            var request = sentItems.OfType<RequestTelemetry>().Single();
            var trace1 = sentItems.OfType<TraceTelemetry>().First();
            var trace2 = sentItems.OfType<TraceTelemetry>().Last();
            Assert.Equal(serviceName, request.Context.Cloud.RoleName);
            Assert.Equal(serviceName, trace1.Context.Cloud.RoleName);
            Assert.Equal(serviceName, trace2.Context.Cloud.RoleName);

            Assert.Equal($"{hostName}.{pid}", request.Context.Cloud.RoleInstance);
            Assert.Equal($"{hostName}.{pid}", trace1.Context.Cloud.RoleInstance);
            Assert.Equal($"{hostName}.{pid}", trace2.Context.Cloud.RoleInstance);

            Assert.Equal(0, request.Properties.Count);
            Assert.Equal(0, trace1.Properties.Count);
            Assert.Equal(0, trace2.Properties.Count);
        }

        */

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependenciesWithAnnotations()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            now = now.GetBefore(TimeSpan.FromSeconds(1));
            name = "spanName";
            kind = SpanKind.Client;

            annotations = TimedEvents<IAnnotation>.Create(
                new List<ITimedEvent<IAnnotation>>() {
                    TimedEvent<IAnnotation>.Create(now.Now, Annotation.FromDescription("test message1")),
                    TimedEvent<IAnnotation>.Create(null, Annotation.FromDescriptionAndAttributes("test message2", new Dictionary<string, IAttributeValue>()
                        {
                            { "custom.stringAttribute", AttributeValue.StringAttributeValue("string") },
                            { "custom.longAttribute", AttributeValue.LongAttributeValue(long.MaxValue) },
                            { "custom.boolAttribute", AttributeValue.BooleanAttributeValue(true) },
                        })),
                },
                droppedEventsCount: 0);

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            Assert.Equal(3, sentItems.Count);
            Assert.Single(sentItems.OfType<DependencyTelemetry>());
            Assert.Equal(2, sentItems.OfType<TraceTelemetry>().Count());

            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            var trace1 = sentItems.OfType<TraceTelemetry>().First();
            var trace2 = sentItems.OfType<TraceTelemetry>().Last();

            Assert.Equal(dependency.Context.Operation.Id, trace1.Context.Operation.Id);
            Assert.Equal(dependency.Context.Operation.Id, trace2.Context.Operation.Id);
            Assert.Equal(dependency.Id, trace1.Context.Operation.ParentId);
            Assert.Equal(dependency.Id, trace2.Context.Operation.ParentId);

            Assert.Equal("test message1", trace1.Message);
            Assert.Equal("test message2", trace2.Message);

            Assert.Equal(now.NowNanos, new TestClock(trace1.Timestamp).NowNanos);
            Assert.NotEqual(now.NowNanos, new TestClock(trace2.Timestamp).NowNanos);
            Assert.True(Math.Abs((DateTime.UtcNow - trace2.Timestamp).TotalSeconds) < 1);

            Assert.False(trace1.Properties.Any());
            Assert.Equal(3, trace2.Properties.Count);
            Assert.True(trace2.Properties.ContainsKey("custom.stringAttribute"));
            Assert.Equal("string", trace2.Properties["custom.stringAttribute"]);

            Assert.True(trace2.Properties.ContainsKey("custom.longAttribute"));
            Assert.Equal(long.MaxValue.ToString(), trace2.Properties["custom.longAttribute"]);

            Assert.True(trace2.Properties.ContainsKey("custom.boolAttribute"));
            Assert.Equal(bool.TrueString, trace2.Properties["custom.boolAttribute"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithMessage()
        {
            this.GetDefaults(out var now, out var context, out var parentSpanId, out var hasRemoteParent, out var name, out var startTimestamp, out var attributes, out var annotations, out var messageOrNetworkEvents, out var links, out var childSpanCount, out var status, out var kind, out var endTimestamp);
            Thread.Sleep(TimeSpan.FromTicks(10));
            name = "spanName";
            kind = SpanKind.Server;

            messageOrNetworkEvents = TimedEvents<IMessageEvent>.Create(
                new List<ITimedEvent<IMessageEvent>>()
                {
                    TimedEvent<IMessageEvent>.Create(now.Now, MessageEvent.Builder(MessageEventType.Received, 1).SetCompressedMessageSize(2).SetUncompressedMessageSize(3).Build()),
                    TimedEvent<IMessageEvent>.Create(now.Now, MessageEvent.Builder(MessageEventType.Sent, 4).SetCompressedMessageSize(5).SetUncompressedMessageSize(6).Build()),
                    TimedEvent<IMessageEvent>.Create(null, MessageEvent.Builder(MessageEventType.Unspecified, 7).SetCompressedMessageSize(8).SetUncompressedMessageSize(9).Build()),
                },
                droppedEventsCount: 0);

            var span = SpanData.Create(context, parentSpanId, hasRemoteParent, name, startTimestamp, attributes, annotations, messageOrNetworkEvents, links, childSpanCount, status, kind, endTimestamp);

            var sentItems = this.ConvertSpan(span);

            Assert.Equal(4, sentItems.Count);
            Assert.Single(sentItems.OfType<RequestTelemetry>());
            Assert.Equal(3, sentItems.OfType<TraceTelemetry>().Count());

            var request = sentItems.OfType<RequestTelemetry>().Single();
            var traces = sentItems.OfType<TraceTelemetry>().ToArray();

            foreach (var t in traces)
            {
                Assert.Equal(request.Context.Operation.Id, t.Context.Operation.Id);
                Assert.Equal(request.Id, t.Context.Operation.ParentId);
                Assert.False(t.Properties.Any());
            }

            Assert.Equal("MessageEvent. messageId: '1', type: 'Received', compressed size: '2', uncompressed size: '3'", traces[0].Message);
            Assert.Equal("MessageEvent. messageId: '4', type: 'Sent', compressed size: '5', uncompressed size: '6'", traces[1].Message);
            Assert.Equal("MessageEvent. messageId: '7', type: 'Unspecified', compressed size: '8', uncompressed size: '9'", traces[2].Message);
        }

        /*
        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksRequestWithCorrectIkey()
        {
            // ARRANGE
            var span = this.CreateBasicSpan(SpanKind.Server, "HttpIn");

            // ACT
            var sentItems = this.ConvertSpan(span, null, "ikey1");

            // ASSERT
            var request = sentItems.OfType<RequestTelemetry>().Single();
            Assert.Equal("ikey1", request.Context.InstrumentationKey);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksDependencyWithCorrectIkey()
        {
            // ARRANGE
            var span = this.CreateBasicSpan(SpanKind.Client, "HttpOut");

            // ACT
            var sentItems = this.ConvertSpan(span, null, "ikey1");

            // ASSERT
            var dependency = sentItems.OfType<DependencyTelemetry>().Single();
            Assert.Equal("ikey1", dependency.Context.InstrumentationKey);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksTraceWithCorrectIkey()
        {
            // ARRANGE
            var now = DateTime.UtcNow;
            var span = this.CreateBasicSpan(SpanKind.Server, "spanName");
            span.TimeEvents = new Span.Types.TimeEvents
            {
                TimeEvent =
                {
                    new Span.Types.TimeEvent
                    {
                        Time = now.ToTimestamp(),
                        Annotation = new Span.Types.TimeEvent.Types.Annotation
                        {
                            Description = new TruncatableString {Value = "test message1"},
                        },
                    },
                    new Span.Types.TimeEvent
                    {
                        Annotation = new Span.Types.TimeEvent.Types.Annotation
                        {
                            Description = new TruncatableString {Value = "test message2"},
                            Attributes = new Span.Types.Attributes
                            {
                                AttributeMap =
                                {
                                    ["custom.stringAttribute"] = this.CreateAttributeValue("string"),
                                    ["custom.longAttribute"] = this.CreateAttributeValue(long.MaxValue),
                                    ["custom.boolAttribute"] = this.CreateAttributeValue(true),
                                },
                            },
                        },
                    },
                },
            };

            // ACT
            var sentItems = this.ConvertSpan(span, null, "ikey1");

            // ASSERT
            var request = sentItems.OfType<RequestTelemetry>().Single();
            var trace1 = sentItems.OfType<TraceTelemetry>().First();
            var trace2 = sentItems.OfType<TraceTelemetry>().Last();

            Assert.Equal("ikey1", request.Context.InstrumentationKey);
            Assert.Equal("ikey1", trace1.Context.InstrumentationKey);
            Assert.Equal("ikey1", trace2.Context.InstrumentationKey);
        }


        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksNodeInfo()
        {
            // ARRANGE
            var start = DateTime.UtcNow;
            string hostName = "host", serviceName = "tests",
                version = "1.2.3.4.5", eventName = "Config", peer = "1.2.3.4:51639";

            uint pid = 12345;
            var lang = LibraryInfo.Types.Language.CSharp;

            var node = CreateBasicNode(hostName, pid, lang, version, serviceName);
            node.Identifier.StartTimestamp = start.ToTimestamp();
            node.Attributes.Add("a", "b");

            // ACT
            this.client.TrackNodeEvent(node, eventName, peer, "ikey1");

            // ASSERT
            Assert.Single(sentItems);
            Assert.IsInstanceOfType(sentItems.Single(), typeof(EventTelemetry));
            var evnt = sentItems.OfType<EventTelemetry>().Single();
            Assert.Equal("ikey1", evnt.Context.InstrumentationKey);
            Assert.Equal($"{eventName}.node", evnt.Name);
            Assert.Equal($"lf_{lang.ToString().ToLower()}-oc:{version}", evnt.Context.GetInternalContext().SdkVersion);
            Assert.Equal(serviceName, evnt.Context.Cloud.RoleName);
            Assert.Equal($"{hostName}.{pid}", evnt.Context.Cloud.RoleInstance);

            Assert.Equal(GetAssemblyVersionString(), evnt.Properties["lf_version"]);
            Assert.Equal(start.ToString("o"), evnt.Properties["process_start_ts"]);
            Assert.Equal(peer, evnt.Properties["peer"]);

            Assert.Equal("b", evnt.Properties["a"]);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksEmptyNodeInfo()
        {
            // ARRANGE
            string eventName = "Config", peer = "1.2.3.4:51639";
            var node = new Node();

            // ACT
            this.client.TrackNodeEvent(node, eventName, peer, "ikey1");

            // ASSERT
            Assert.Single(sentItems);
            Assert.IsInstanceOfType(sentItems.Single(), typeof(EventTelemetry));
            var evnt = sentItems.OfType<EventTelemetry>().Single();
            Assert.Equal("ikey1", evnt.Context.InstrumentationKey);
            Assert.Equal($"{eventName}.node", evnt.Name);
            Assert.Equal("lf_unspecified-oc:0.0.0", evnt.Context.GetInternalContext().SdkVersion);
            Assert.Null(evnt.Context.Cloud.RoleName);
            Assert.Equal(peer, evnt.Properties["peer"]);
            Assert.Equal(1, evnt.Properties.Count);
        }

        [Fact]
        public void OpenCensusTelemetryConverterTests_TracksHalfEmptyNodeInfo()
        {
            // ARRANGE
            string eventName = "Config", peer = "1.2.3.4:51639";
            var node = new Node
            {
                Identifier = new ProcessIdentifier { Pid = 1 },
                LibraryInfo = new LibraryInfo { ExporterVersion = "1", CoreLibraryVersion = "2" },
            };

            // ACT
            this.client.TrackNodeEvent(node, eventName, peer, "ikey1");

            // ASSERT
            Assert.Single(sentItems);
            Assert.True(sentItems.Single() is EventTelemetry);
            var evnt = sentItems.OfType<EventTelemetry>().Single();
            Assert.Equal("ikey1", evnt.Context.InstrumentationKey);
            Assert.Equal($"{eventName}.node", evnt.Name);
            //Assert.Equal("lf_unspecified-oc:2", evnt.Context.GetInternalContext().SdkVersion);
            Assert.Null(evnt.Context.Cloud.RoleName);
            Assert.Equal(peer, evnt.Properties["peer"]);
            Assert.Equal("1", evnt.Properties["oc_exporter_version"]);
            Assert.Equal(GetAssemblyVersionString(), evnt.Properties["lf_version"]);
            Assert.Equal(3, evnt.Properties.Count);

            Assert.Equal(".1", evnt.Context.Cloud.RoleInstance);
        }

        private Node CreateBasicNode(string hostName, uint pid, LibraryInfo.Types.Language lang, string version, string serviceName)
        {
            return new Node
            {
                Identifier = new ProcessIdentifier
                {
                    HostName = hostName,
                    Pid = pid,
                },
                LibraryInfo = new LibraryInfo
                {
                    Language = lang,
                    CoreLibraryVersion = version,
                },
                ServiceInfo = new ServiceInfo
                {
                    Name = serviceName,
                },
            };
        }

    */

        private static (string, byte[]) GenerateRandomId(int byteCount)
        {
            var idBytes = new byte[byteCount];
            Rand.NextBytes(idBytes);

            var idString = BitConverter.ToString(idBytes).Replace("-", "").ToLower();

            return (idString, idBytes);
        }

        private static readonly Random Rand = new Random();

        /*
        internal static string GetAssemblyVersionString()
        {
            // Since dependencySource is no longer set, sdk version is prepended 
            // with information which can identify whether RDD was collected by profiler/framework
            // For directly using TrackDependency(), version will be simply what is set by core
            Type converterType = typeof(OpenCensusTelemetryConverterTests);

            object[] assemblyCustomAttributes = converterType.Assembly.GetCustomAttributes(false);
            string versionStr = assemblyCustomAttributes
                .OfType<AssemblyFileVersionAttribute>()
                .First()
                .Version;

            Version version = new Version(versionStr);

            string postfix = version.Revision.ToString(CultureInfo.InvariantCulture);
            return version.ToString(3) + "-" + postfix;
        }
        */

        private void GetDefaults(
            out TestClock now,
            out ISpanContext context,
            out ISpanId parentSpanId,
            out bool? hasRemoteParent,
            out string name,
            out Timestamp startTimestamp,
            out IAttributes attributes,
            out ITimedEvents<IAnnotation> annotations,
            out ITimedEvents<IMessageEvent> messageOrNetworkEvents,
            out ILinks links,
            out int? childSpanCount,
            out Status status,
            out SpanKind kind,
            out Timestamp endTimestamp)
        {
            now = new TestClock(DateTimeOffset.UtcNow);
            context = SpanContext.Create(TraceId.FromBytes(this.testTraceIdBytes), SpanId.FromBytes(this.testSpanIdBytes), TraceOptions.Default, Tracestate.Empty);
            parentSpanId = SpanId.Invalid;
            hasRemoteParent = null;
            name = "spanName";
            startTimestamp = now.GetBefore(TimeSpan.FromSeconds(1)).Now;
            attributes = null;
            annotations = null;
            messageOrNetworkEvents = null;
            links = null;
            childSpanCount = null;
            status = null;
            kind = SpanKind.Server;
            endTimestamp = now.Now;
        }
    }
};