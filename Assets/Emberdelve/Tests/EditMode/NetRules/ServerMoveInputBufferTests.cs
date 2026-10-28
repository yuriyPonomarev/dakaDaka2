using Emberdelve.Net.Rules;
using NUnit.Framework;

namespace Emberdelve.NetRules.Tests
{
    public sealed class ServerMoveInputBufferTests
    {
        private static MoveInputFrame F(uint seq, float y = 1f, float yaw = 0f, byte flags = 0)
        {
            return new MoveInputFrame { Seq = seq, MoveY = y, Yaw = yaw, Flags = flags };
        }

        [Test]
        public void Queued_FramesComeOutOnePerCall_InOrder()
        {
            ServerMoveInputBuffer b = new ServerMoveInputBuffer();
            b.Push(F(1));
            b.Push(F(2));
            b.Push(F(3));
            Assert.That(b.Depth, Is.EqualTo(3));

            Assert.That(b.Next(0f, 0f, out MoveInputFrame f1), Is.EqualTo(MoveFrameSource.Queued));
            Assert.That(f1.Seq, Is.EqualTo(1u));
            Assert.That(b.Next(0f, 0f, out MoveInputFrame f2), Is.EqualTo(MoveFrameSource.Queued));
            Assert.That(f2.Seq, Is.EqualTo(2u));
            Assert.That(b.Depth, Is.EqualTo(1));
        }

        [Test]
        public void Missing_RepeatsTheLastMoveThreeTicks_ThenGoesNeutral_KeepingTheView()
        {
            ServerMoveInputBuffer b = new ServerMoveInputBuffer();
            byte flags = (byte)(MoveInputFrame.FlagSprint | MoveInputFrame.FlagJump | MoveInputFrame.FlagCrouchToggle);
            b.Push(F(1, 1f, 90f, flags));
            b.Next(0f, 0f, out _);

            for (int i = 0; i < ServerMoveInputBuffer.MaxRepeatTicks; i++)
            {
                Assert.That(b.Next(0f, 0f, out MoveInputFrame rep), Is.EqualTo(MoveFrameSource.Repeated), "repeat " + i);
                Assert.That(rep.MoveY, Is.EqualTo(1f));
                Assert.That(rep.Yaw, Is.EqualTo(90f));
                Assert.That(rep.Sprint, Is.True, "held state is repeated");
                Assert.That(rep.Jump, Is.False, "an edge is never repeated");
                Assert.That(rep.CrouchToggle, Is.False);
            }

            Assert.That(b.Next(5f, 5f, out MoveInputFrame neutral), Is.EqualTo(MoveFrameSource.Neutral));
            Assert.That(neutral.MoveX, Is.EqualTo(0f));
            Assert.That(neutral.MoveY, Is.EqualTo(0f));
            Assert.That(neutral.Flags, Is.EqualTo((byte)0));
            Assert.That(neutral.Yaw, Is.EqualTo(90f), "the view is kept, not reset");
            Assert.That(b.Next(5f, 5f, out _), Is.EqualTo(MoveFrameSource.Neutral));
            Assert.That(b.RepeatedTicks, Is.EqualTo(3));
            Assert.That(b.NeutralTicks, Is.EqualTo(2));
        }

        [Test]
        public void NothingEverReceived_IsNeutral_WithTheFallbackView()
        {
            ServerMoveInputBuffer b = new ServerMoveInputBuffer();
            Assert.That(b.Next(33f, 7f, out MoveInputFrame f), Is.EqualTo(MoveFrameSource.Neutral));
            Assert.That(f.MoveY, Is.EqualTo(0f));
            Assert.That(f.Yaw, Is.EqualTo(33f));
            Assert.That(f.Pitch, Is.EqualTo(7f));
        }

        [Test]
        public void ANewFrame_ResetsTheRepeatBudget()
        {
            ServerMoveInputBuffer b = new ServerMoveInputBuffer();
            b.Push(F(1));
            b.Next(0f, 0f, out _);
            for (int i = 0; i < 5; i++) b.Next(0f, 0f, out _);
            Assert.That(b.Next(0f, 0f, out _), Is.EqualTo(MoveFrameSource.Neutral));

            b.Push(F(2));
            Assert.That(b.Next(0f, 0f, out _), Is.EqualTo(MoveFrameSource.Queued));
            Assert.That(b.Next(0f, 0f, out _), Is.EqualTo(MoveFrameSource.Repeated), "the budget is full again");
        }

        [Test]
        public void Burst_IsBounded_OldestDropped_EdgesFolded()
        {
            ServerMoveInputBuffer b = new ServerMoveInputBuffer();
            b.Push(F(1, 1f, 0f, MoveInputFrame.FlagJump));
            for (uint i = 2; i <= 5; i++) b.Push(F(i));

            Assert.That(b.Depth, Is.EqualTo(ServerMoveInputBuffer.TrimDepth));
            Assert.That(b.Overflowed, Is.EqualTo(1));

            b.Next(0f, 0f, out MoveInputFrame first);
            Assert.That(first.Seq, Is.EqualTo(2u), "frame 1 was the oldest and is gone");
            Assert.That(first.Jump, Is.True, "its jump press was folded into the next frame instead of being lost");

            for (uint i = 6; i <= 40; i++) b.Push(F(i));
            Assert.That(b.Depth, Is.LessThanOrEqualTo(ServerMoveInputBuffer.Capacity));
            Assert.That(ServerMoveInputBuffer.Capacity, Is.EqualTo(6));
        }
    }
}
