﻿namespace OpenCensus.Stats
{
    using System;

    public sealed class StatsRecorder : StatsRecorderBase
    {
        private StatsManager statsManager;

        internal StatsRecorder(StatsManager statsManager)
        {
            if (statsManager == null)
            {
                throw new ArgumentNullException(nameof(statsManager));
            }

            this.statsManager = statsManager;
        }

        public override IMeasureMap NewMeasureMap()
        {
            return MeasureMap.Create(statsManager);
        }
    }
}
