﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Steeltoe.Management.Census.Stats.Test
{
    public class StatsTest
    {
        [Fact(Skip = "Fix statics usage")]
        public void GetStatsRecorder()
        {
            Assert.Equal(typeof(StatsRecorder), Stats.StatsRecorder.GetType());
        }

        [Fact(Skip = "Fix statics usage")]
        public void GetViewManager()
        {
            Assert.Equal(typeof(ViewManager), Stats.ViewManager.GetType());
        }
    }
}
