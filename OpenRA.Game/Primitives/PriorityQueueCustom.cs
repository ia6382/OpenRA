//-----------------------------------------------------------------------
//
//  Microsoft Windows Client Platform
//  Copyright (C) Microsoft Corporation. All rights reserved.
//
//  File:      PriorityQueue.cs
//
//  Contents:  Implementation of PriorityQueue class.
//
//  Created:   2-14-2005 Niklas Borson (niklasb)
//  Customized: 5-14-2021 Ivan Antešić
//------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace OpenRA.Primitives
{
	/// <summary>
	/// PriorityQueue provides a stack-like interface, except that objects
	/// "pushed" in arbitrary order are "popped" in order of priority, i.e.,
	/// from least to greatest as defined by the specified comparer.
	/// </summary>
	/// <remarks>
	/// Push and Pop are each O(log N). Pushing N objects and them popping
	/// them all is equivalent to performing a heap sort and is O(N log N).
	/// </remarks>
	public class PriorityQueueCustom<T> : IPriorityQueue<T>
	{
		// The _heap array represents a binary tree with the "shape" property.
		// If we number the nodes of a binary tree from left-to-right and top-
		// to-bottom as shown,
		//
		//             0
		//           /   \
		//          /     \
		//         1       2
		//       /  \     / \
		//      3    4   5   6
		//     /\    /
		//    7  8  9
		//
		// The shape property means that there are no gaps in the sequence of
		// numbered nodes, i.e., for all N > 0, if node N exists then node N-1
		// also exists. For example, the next node added to the above tree would
		// be node 10, the right child of node 4.
		//
		// Because of this constraint, we can easily represent the "tree" as an
		// array, where node number == array index, and parent/child relationships
		// can be calculated instead of maintained explicitly. For example, for
		// any node N > 0, the parent of N is at array index (N - 1) / 2.
		//
		// In addition to the above, the first _count members of the _heap array
		// compose a "heap", meaning each child node is greater than or equal to
		// its parent node; thus, the root node is always the minimum (i.e., the
		// best match for the specified style, weight, and stretch) of the nodes
		// in the heap.
		//
		// Initially _count < 0, which means we have not yet constructed the heap.
		// On the first call to MoveNext, we construct the heap by "pushing" all
		// the nodes into it. Each successive call "pops" a node off the heap
		// until the heap is empty (_count == 0), at which time we've reached the
		// end of the sequence.
		private const int DefaultCapacity = 6;
		private List<T> heap;
		private IComparer<T> comparer;

		/// <summary>
		/// Gets the number of items in the priority queue.
		/// </summary>
		internal int Count { get; private set; }

		public PriorityQueueCustom()
			: this(Comparer<T>.Default) { }

		public PriorityQueueCustom(IComparer<T> comparer)
		{
			// heap = new T[capacity > 0 ? capacity : DefaultCapacity];
			heap = new List<T>();
			Count = 0;
			this.comparer = comparer;
		}

		public bool Empty { get { return Count == 0; } }

		/// <summary>
		/// Gets the first or topmost object in the priority queue, which is the
		/// object with the minimum value.
		/// </summary>
		public T Peek()
		{
			if (Count <= 0)
				throw new InvalidOperationException("PriorityQueue empty.");

			return heap[0];
		}

		/// <summary>
		/// Adds an object to the priority queue.
		/// </summary>
		public void Add(T value)
		{
			heap.Add(default(T));
			SiftUp(Count, ref value, 0);
			Count++;
		}

		/// <summary>
		/// Removes the first node (i.e., the logical root) from the heap.
		/// </summary>
		public T Pop()
		{
			if (Count <= 0)
				throw new InvalidOperationException("PriorityQueue empty.");

			var root = heap[0];

			--Count;

			// discarding the root creates a gap at position 0.  We fill the
			// gap with the item x from the last position, after first sifting
			// the gap to a position where inserting x will maintain the
			// heap property.  This is done in two phases - SiftDown and SiftUp.
			//
			// The one-phase method found in many textbooks does 2 comparisons
			// per level, while this method does only 1.  The one-phase method
			// examines fewer levels than the two-phase method, but it does
			// more comparisons unless x ends up in the top 2/3 of the tree.
			// That accounts for only n^(2/3) items, and x is even more likely
			// to end up near the bottom since it came from the bottom in the
			// first place.  Overall, the two-phase method is noticeably better.
			T x = heap[Count];        // lift item x out from the last position
			int index = SiftDown(0);    // sift the gap at the root down to the bottom
			SiftUp(index, ref x, 0);    // sift the gap up, and insert x in its rightful position
			heap.RemoveAt(heap.Count - 1);

			return root;
		}

		// sift a gap at the given index down to the bottom of the heap,
		// return the resulting index
		private int SiftDown(int index)
		{
			// Loop invariants:
			//
			//  1.  parent is the index of a gap in the logical tree
			//  2.  leftChild is
			//      (a) the index of parent's left child if it has one, or
			//      (b) a value >= _count if parent is a leaf node
			int parent = index;
			int leftChild = HeapLeftChild(parent);

			while (leftChild < Count)
			{
				int rightChild = HeapRightFromLeft(leftChild);
				int bestChild =
					(rightChild < Count && comparer.Compare(heap[rightChild], heap[leftChild]) < 0) ?
					rightChild : leftChild;

				// Promote bestChild to fill the gap left by parent.
				heap[parent] = heap[bestChild];

				// Restore invariants, i.e., let parent point to the gap.
				parent = bestChild;
				leftChild = HeapLeftChild(parent);
			}

			return parent;
		}

		// sift a gap at index up until it reaches the correct position for x,
		// or reaches the given boundary.  Place x in the resulting position.
		private void SiftUp(int index, ref T x, int boundary)
		{
			while (index > boundary)
			{
				int parent = HeapParent(index);
				if (comparer.Compare(heap[parent], x) > 0)
				{
					heap[index] = heap[parent];
					index = parent;
				}
				else
				{
					break;
				}
			}

			heap[index] = x;
		}

		/// <summary>
		/// Calculate the parent node index given a child node's index, taking advantage
		/// of the "shape" property.
		/// </summary>
		private static int HeapParent(int i)
		{
			return (i - 1) / 2;
		}

		/// <summary>
		/// Calculate the left child's index given the parent's index, taking advantage of
		/// the "shape" property. If there is no left child, the return value is >= _count.
		/// </summary>
		private static int HeapLeftChild(int i)
		{
			return (i * 2) + 1;
		}

		/// <summary>
		/// Calculate the right child's index from the left child's index, taking advantage
		/// of the "shape" property (i.e., sibling nodes are always adjacent). If there is
		/// no right child, the return value >= _count.
		/// </summary>
		private static int HeapRightFromLeft(int i)
		{
			return i + 1;
		}
	}
}
