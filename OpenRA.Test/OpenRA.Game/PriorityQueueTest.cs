#region Copyright & License Information
/*
 * Copyright 2007-2020 The OpenRA Developers (see AUTHORS)
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpenRA.Primitives;

namespace OpenRA.Test
{
	[TestFixture]
	class PriorityQueueTest
	{
		[TestCase(TestName = "PriorityQueue maintains invariants when adding and removing items.")]
		public void PriorityQueueGeneralTest()
		{
			var queue = new PriorityQueueCustom<int>();

			Assert.IsTrue(queue.Empty, "New queue should start out empty.");
			Assert.Throws<InvalidOperationException>(() => queue.Peek(), "Peeking at an empty queue should throw.");
			Assert.Throws<InvalidOperationException>(() => queue.Pop(), "Popping an empty queue should throw.");

			int[] test = new int[100];
			Random randNum = new Random();
			for (int i = 0; i < test.Length; i++)
			{
				test[i] = randNum.Next(600, 1200);
			}

			// foreach (var value in new[] { 4, 3, 5, 1, 2 })
			// foreach (var value in new[] { 686, 828, 827, 684, 971, 642, 786, 928, 927, 784, 1071, 742, 1029, 888, 968, 888, 886, 1028, 1027, 884, 1171, 842, 988, 1068, 988, 986, 1128, 1127, 984, 1271, 942, 1087, 1167, 1087, 985 })
			foreach (var value in test)
			{
				queue.Add(value);
				Assert.IsFalse(queue.Empty, "Queue should not be empty - items have been added.");
			}

			Array.Sort(test);

			// foreach (var value in new[] { 1, 2, 3, 4, 5 })
			// foreach (var value in new[] { 642, 684, 686, 742, 784, 786, 827, 828, 842, 884, 886, 888, 888, 927, 928, 942, 968, 971, 984, 985, 986, 988, 988, 1027, 1028, 1029, 1068, 1071, 1087, 1087, 1127, 1128, 1167, 1171, 1271 })
			foreach (var value in test)
			{
				// Assert.AreEqual(value, queue.Peek(), "Peek returned the wrong item - should be in order.");
				Assert.IsFalse(queue.Empty, "Queue should not be empty yet.");
				Assert.AreEqual(value, queue.Pop(), "Pop returned the wrong item - should be in order.");
			}

			Assert.IsTrue(queue.Empty, "Queue should now be empty.");
			Assert.Throws<InvalidOperationException>(() => queue.Peek(), "Peeking at an empty queue should throw.");
			Assert.Throws<InvalidOperationException>(() => queue.Pop(), "Popping an empty queue should throw.");
		}
	}
}
