using System;
using Xunit;
using MuNTrade.Core.Common;

namespace MuNTrade.Core.Tests.Common
{
    public class TimeDecayStatsTests
    {
        [Fact]
        public void ECO_Validation_OfiSlow_Decay()
        {
            // Requirement from ECO: 
            // "Establish a Mock Tick sequence: input continuous +100 OFI, then pause 60s, then input a 0. 
            // Confirm OfiSlow decays."

            var stats = new TimeDecayStats(60.0); // 60s Half-Life

            // 1. Continuous Input (+100)
            // Initial
            stats.Update(100, 0); 
            Assert.Equal(100, stats.Value);

            // Stabilize
            for(int i=0; i<10; i++)
            {
                stats.Update(100, 1.0); // 1 sec delta
                Assert.True(stats.Value > 99, "Value should stay near 100 on constant input");
            }
            Assert.Equal(100, stats.Value, 1); // should be very close to 100

            // 2. Pause 60s then Input 0
            // In Event-Driven model, "Pause" means no updates.
            // Then "Input a 0" implies the next event has value 0 and delta time = 60s.
            
            stats.Update(0, 60.0); 

            // 3. Confirm Decay
            // Half-Life is 60s. After 60s decay, value should be 50% of previous.
            // Previous = 100. Target = 0.
            // val = val + alpha * (target - val)
            // alpha = 1 - exp(-ln(2)/60 * 60) = 1 - 0.5 = 0.5
            // val = 100 + 0.5 * (0 - 100) = 50.
            
            Assert.Equal(50, stats.Value, 0.1);

            // Verify further decay
            stats.Update(0, 60.0);
            Assert.Equal(25, stats.Value, 0.1);
        }

        [Fact]
        public void Basic_Decay_1sec()
        {
            var stats = new TimeDecayStats(1.0); // 1s Half-Life

            stats.Update(100, 0);
            Assert.Equal(100, stats.Value);

            // 1s elapsed -> should be 50
            stats.Update(0, 1.0);
            Assert.Equal(50, stats.Value, 0.001);

            // Another 1s -> should be 25
            stats.Update(0, 1.0);
            Assert.Equal(25, stats.Value, 0.001);
        }

        [Fact]
        public void Cap_At_60s_Logic_Check()
        {
             // Although the class itself doesn't have the cap (OrderBookEngine enforces it),
             // The TimeDecayStats should handle large deltas correctly if passed (e.g. approaching 0).
             var stats = new TimeDecayStats(10.0); 
             stats.Update(100, 0);

             // Very long time: 1000s (much larger than half-life 10s)
             // Value should decay to effectively 0
             stats.Update(0, 1000.0);
             Assert.Equal(0, stats.Value, 0.0001);
        }
    }
}

