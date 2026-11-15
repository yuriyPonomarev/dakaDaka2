using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Net;
using Emberdelve.Net.Rules;
using NUnit.Framework;
using UnityEngine;
namespace Emberdelve.Tests
{
    public sealed class NetDrawCancellationTests
    {
        private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic;
        [TestCase(false)]
        [TestCase(true)]
        public void SuppressionPurgesUnsentDrawAndReload_AndReservesCancellationOutsideFullQueue(bool queuedDraw)
        {
            var go=new GameObject("draw-cancel-test");
            try
            {
                var sender=go.AddComponent<NetCombatSender>();
                var type=typeof(NetCombatSender);
                var queue=(Queue<CombatIntentKind>)type.GetField("_queue",Fields).GetValue(sender);
                if(queuedDraw)
                {
                    queue.Enqueue(CombatIntentKind.DrawStart);
                    queue.Enqueue(CombatIntentKind.DrawRelease);
                    queue.Enqueue(CombatIntentKind.Reload);
                }
                while(queue.Count<CombatNetLimits.ClientQueueLimit) queue.Enqueue(CombatIntentKind.GuardUp);
                type.GetField("_drawHeld",Fields).SetValue(sender,true);
                type.GetMethod("CancelDrawInput",Fields).Invoke(sender,null);
                Assert.That(type.GetField("_drawHeld",Fields).GetValue(sender),Is.False);
                Assert.That(type.GetField("_drawCancelPending",Fields).GetValue(sender),Is.True);
                Assert.That(queue.Count,Is.EqualTo(CombatNetLimits.ClientQueueLimit-(queuedDraw?3:0)));
                foreach(var item in queue) Assert.That(item,Is.EqualTo(CombatIntentKind.GuardUp));
                type.GetMethod("CancelDrawInput",Fields).Invoke(sender,null);
                Assert.That(type.GetField("_drawCancelPending",Fields).GetValue(sender),Is.True,"not lost while waiting for send interval");
                type.GetField("_drawCancelPending",Fields).SetValue(sender,false);
                type.GetMethod("CancelDrawInput",Fields).Invoke(sender,null);
                Assert.That(type.GetField("_drawCancelPending",Fields).GetValue(sender),Is.False,"continued UI focus must not flood cancellations");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
