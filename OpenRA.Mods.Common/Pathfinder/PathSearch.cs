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
using System.Linq;
using System.Runtime.CompilerServices;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Pathfinder
{
	public sealed class PathSearch : BasePathSearch
	{
		// PERF: Maintain a pool of layers used for paths searches for each world. These searches are performed often
		// so we wish to avoid the high cost of initializing a new search space every time by reusing the old ones.
		static readonly ConditionalWeakTable<World, CellInfoLayerPool> LayerPoolTable = new ConditionalWeakTable<World, CellInfoLayerPool>();
		static readonly ConditionalWeakTable<World, CellInfoLayerPool>.CreateValueCallback CreateLayerPool = world => new CellInfoLayerPool(world.Map);

		static CellInfoLayerPool LayerPoolForWorld(World world)
		{
			return LayerPoolTable.GetValue(world, CreateLayerPool);
		}

		public override IEnumerable<(CPos, int)> Considered
		{
			get { return considered; }
		}

		LinkedList<(CPos, int)> considered;

		#region Constructors

		private PathSearch(IGraph<CellInfo> graph)
			: base(graph)
		{
			considered = new LinkedList<(CPos, int)>();
		}

		public static IPathSearch Search(World world, Locomotor locomotor, Actor self, BlockedByActor check, Func<CPos, bool> goalCondition)
		{
			var graph = new PathGraph(LayerPoolForWorld(world), locomotor, self, world, check);
			var search = new PathSearch(graph);
			search.isGoal = goalCondition;
			search.heuristic = loc => 0;
			return search;
		}

		public static IPathSearch FromPoint(World world, Locomotor locomotor, Actor self, CPos @from, CPos target, BlockedByActor check)
		{
			return FromPoints(world, locomotor, self, new[] { from }, target, check);
		}

		public static IPathSearch FromPoints(World world, Locomotor locomotor, Actor self, IEnumerable<CPos> froms, CPos target, BlockedByActor check)
		{
			var graph = new PathGraph(LayerPoolForWorld(world), locomotor, self, world, check);
			var search = new PathSearch(graph);

			// SLO: quick fix for actors that pathfind before spawning in world or have multiple froms - harvesters
			if (froms.Count() == 1)
			{
				var rraSearch = self.Trait<Mobile>().RRAsearch;
				search.heuristic = search.RRA(rraSearch);
				search.isInRAA = search.IsInRRA(rraSearch);
			}
			else
				search.heuristic = search.DefaultEstimator(target);

			// The search will aim for the shortest path by default, a weight of 100%.
			// We can allow the search to find paths that aren't optimal by changing the weight.
			// We provide a weight that limits the worst case length of the path,
			// e.g. a weight of 110% will find a path no more than 10% longer than the shortest possible.
			// The benefit of allowing the search to return suboptimal paths is faster computation time.
			// The search can skip some areas of the search space, meaning it has less work to do.
			// We allow paths up to 25% longer than the shortest, optimal path, to improve pathfinding time.
			search.heuristicWeightPercentage = 100; // SLO: popravi na 100, prej je bilo 125

			search.isGoal = loc =>
			{
				var locInfo = search.Graph[loc];
				return locInfo.EstimatedTotal - locInfo.CostSoFar == 0;
			};

			foreach (var sl in froms)
				if (world.Map.Contains(sl))
					search.AddInitialCell(sl);

			return search;
		}

		public static IPathSearch InitialiseRRA(World world, Locomotor locomotor, Actor self, CPos from, CPos target, BlockedByActor check)
		{
			var graph = new PathGraph(LayerPoolForWorld(world), locomotor, self, world, check);
			graph.IgnoreActor = self;
			var search = new PathSearch(graph);

			search.heuristic = search.DefaultEstimator(from);

			search.heuristicWeightPercentage = 100;

			search.isGoal = loc =>
			{
				var locInfo = search.Graph[loc];
				return locInfo.EstimatedTotal - locInfo.CostSoFar == 0;
			};

			search.AddInitialCell(target);

			return search;
		}

		protected override void AddInitialCell(CPos location)
		{
			var cost = heuristic(location);
			Graph[location] = new CellInfo(0, cost, location, CellStatus.Open);
			var connection = new GraphConnection(location, cost);
			OpenQueue.Add(connection);
			StartPoints.Add(connection);
			considered.AddLast((location, 0));
		}

		#endregion

		/// <summary>
		/// This function analyzes the neighbors of the most promising node in the Pathfinding graph
		/// using the A* algorithm (A-star) and returns that node
		/// </summary>
		/// <returns>The most promising node of the iteration</returns>
		public override CPos Expand()
		{
			var currentMinNode = OpenQueue.Pop().Destination; // SLO: CPos.
			var currentCell = Graph[currentMinNode]; // SLO: CellInfo.

			if (IsTarget(currentMinNode))
			{
				// So we can resume RRA* next time in case OpenQueue is empty
				if (OpenQueue.Empty)
					OpenQueue.Add(new GraphConnection(currentMinNode, currentCell.EstimatedTotal));
				return currentMinNode;
			}

			// Check for duplicates and mark them as invalids
			if (currentCell.Status == CellStatus.Duplicate)
				Graph[currentMinNode] = new CellInfo(currentCell.CostSoFar, currentCell.EstimatedTotal, currentCell.PreviousPos, CellStatus.Invalid);
			else if (currentCell.Status == CellStatus.Invalid)
				return currentMinNode;
			else
				Graph[currentMinNode] = new CellInfo(currentCell.CostSoFar, currentCell.EstimatedTotal, currentCell.PreviousPos, CellStatus.Closed);

			if (Graph.CustomCost != null && Graph.CustomCost(currentMinNode) == PathGraph.CostForInvalidCell)
				return currentMinNode;

			foreach (var connection in Graph.GetConnections(currentMinNode))
			{
				// Calculate the cost up to that point
				var gCost = currentCell.CostSoFar + connection.Cost;

				var neighborCPos = connection.Destination;
				var neighborCell = Graph[neighborCPos];

				// Cost is even higher; next direction:
				if (neighborCell.Status == CellStatus.Closed || gCost >= neighborCell.CostSoFar)
					continue;

				// Now we may seriously consider this direction using heuristics. If the cell has
				// already been processed, we can reuse the result (just the difference between the
				// estimated total and the cost so far
				int hCost;
				if (neighborCell.Status == CellStatus.Open)
					hCost = neighborCell.EstimatedTotal - neighborCell.CostSoFar;
				else
					hCost = heuristic(neighborCPos);

				var estimatedCost = gCost + hCost;

				// Mark duplicates in priority queue
				if (neighborCell.Status == CellStatus.Open || neighborCell.Status == CellStatus.Duplicate || neighborCell.Status == CellStatus.Invalid)
					Graph[neighborCPos] = new CellInfo(gCost, estimatedCost, currentMinNode, CellStatus.Duplicate);
				else
					Graph[neighborCPos] = new CellInfo(gCost, estimatedCost, currentMinNode, CellStatus.Open);

				OpenQueue.Add(new GraphConnection(neighborCPos, estimatedCost));

				if (Debug)
				{
					if (gCost > MaxCost)
						MaxCost = gCost;

					considered.AddLast((neighborCPos, gCost));
				}
			}

			return currentMinNode;
		}

		public override CPos ExpandWHCA(CPos goal)
		{
			// var tmp = Tick;
			// var tmp2 = SpaceTimeReservation.Check(1, 1, Tick);
			var currentMinNode = OpenQueue.Pop().Destination;
			var currentCell = Graph[currentMinNode];

			// Check for duplicates and mark them as invalids
			if (currentCell.Status == CellStatus.Duplicate)
				Graph[currentMinNode] = new CellInfo(currentCell.CostSoFar, currentCell.EstimatedTotal, currentCell.PreviousPos, CellStatus.Invalid);
			else if (currentCell.Status == CellStatus.Invalid)
				return currentMinNode;
			else
				Graph[currentMinNode] = new CellInfo(currentCell.CostSoFar, currentCell.EstimatedTotal, currentCell.PreviousPos, CellStatus.Closed);

			if (Graph.CustomCost != null && Graph.CustomCost(currentMinNode) == PathGraph.CostForInvalidCell)
				return currentMinNode;

			// Optimization: consider only neighbours already processed by RRA (if possible)
			var neighbours = Graph.GetConnections(currentMinNode);
			var rraNodes = neighbours.Where(x => isInRAA(x.Destination) && x.Destination != currentMinNode && x.Destination != currentCell.PreviousPos);
			if (rraNodes.Any())
				neighbours = rraNodes.ToList();
			else { } // breakpoint stop

			foreach (var connection in neighbours)
			{
				var neighborCPos = connection.Destination;
				var neighborCell = Graph[neighborCPos];

				// Calculate the cost up to that point
				var gCost = currentCell.CostSoFar;
				if (currentMinNode != goal || neighborCPos != goal) // SLO: WHCA* edge cost for staying at goal should be 0
					gCost += connection.Cost;

				// Cost is even higher; next direction:
				if (gCost > neighborCell.CostSoFar)
					continue;

				// Now we may seriously consider this direction using heuristics. If the cell has
				// already been processed, we can reuse the result (just the difference between the
				// estimated total and the cost so far
				int hCost;
				if (neighborCell.Status == CellStatus.Open)
					hCost = neighborCell.EstimatedTotal - neighborCell.CostSoFar;
				else
					hCost = heuristic(neighborCPos);

				var estimatedCost = gCost + hCost;

				// Mark duplicates in priority queue
				if (neighborCell.Status == CellStatus.Open || neighborCell.Status == CellStatus.Duplicate || neighborCell.Status == CellStatus.Invalid)
					Graph[neighborCPos] = new CellInfo(gCost, estimatedCost, currentMinNode, CellStatus.Duplicate);
				else
					Graph[neighborCPos] = new CellInfo(gCost, estimatedCost, currentMinNode, CellStatus.Open);

				OpenQueue.Add(new GraphConnection(neighborCPos, estimatedCost));

				if (Debug)
				{
					if (gCost > MaxCost)
						MaxCost = gCost;

					considered.AddLast((neighborCPos, gCost));
				}
			}

			return currentMinNode;
		}
	}
}
