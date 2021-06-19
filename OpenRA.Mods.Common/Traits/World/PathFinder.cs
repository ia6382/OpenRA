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

using System.Collections.Generic;
using System.Linq;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Calculates routes for mobile units based on the A* search algorithm.", " Attach this to the world actor.")]
	public class PathFinderInfo : TraitInfo, Requires<LocomotorInfo>
	{
		public override object Create(ActorInitializer init)
		{
			return new PathFinderUnitPathCacheDecorator(new PathFinder(init.World), new PathCacheStorage(init.World));
		}
	}

	public interface IPathFinder
	{
		/// <summary>
		/// Calculates a path for the actor from source to destination
		/// </summary>
		/// <returns>A path from start to target</returns>
		List<CPos> FindUnitPath(CPos source, CPos target, Actor self, Actor ignoreActor, BlockedByActor check);

		/// <summary>
		/// Calculates a path for the actor from source to destination for next W (window lenght) steps
		/// </summary>
		/// <returns>A W steps long path from start to target</returns>
		List<CPos> FindUnitPathWHCA(CPos source, CPos target, Actor self, Actor ignoreActor, BlockedByActor check, int wSteps);

		List<CPos> FindUnitPathToRange(CPos source, SubCell srcSub, WPos target, WDist range, Actor self, BlockedByActor check, int wLimit);

		/// <summary>
		/// Calculates a path given a search specification
		/// </summary>
		List<CPos> FindPath(IPathSearch search);

		/// <summary>
		/// Calculates a path given a search specification for next W (window lenght) steps
		/// </summary>
		List<CPos> FindPathWHCA(IPathSearch search, CPos goal, int wLimit);

		/// <summary>
		/// Calculates a path given two search specifications, and
		/// then returns a path when both search intersect each other
		/// TODO: This should eventually disappear
		/// </summary>
		List<CPos> FindBidiPath(IPathSearch fromSrc, IPathSearch fromDest);
	}

	public class PathFinder : IPathFinder
	{
		static readonly List<CPos> EmptyPath = new List<CPos>(0);
		readonly World world;
		DomainIndex domainIndex;
		bool cached;

		public PathFinder(World world)
		{
			this.world = world;
		}

		public List<CPos> FindUnitPath(CPos source, CPos target, Actor self, Actor ignoreActor, BlockedByActor check)
		{
			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			var locomotor = ((Mobile)self.OccupiesSpace).Locomotor;

			if (!cached)
			{
				domainIndex = world.WorldActor.TraitOrDefault<DomainIndex>();
				cached = true;
			}

			// If a water-land transition is required, bail early
			if (domainIndex != null && !domainIndex.IsPassable(source, target, locomotor))
				return EmptyPath;

			var distance = source - target;
			var canMoveFreely = locomotor.CanMoveFreelyInto(self, target, check, null);

			// If target is neighbouring cell.
			if (distance.LengthSquared < 3 && !canMoveFreely)
				return new List<CPos> { };

			if (source.Layer == target.Layer && distance.LengthSquared < 3 && canMoveFreely)
				return new List<CPos> { target };

			List<CPos> pb;

			// SLO: Poklice staticno metodo From point iz classa PathSearch, ki vrne search objekt, nad katerim klicemo metodo WithIgnoredActor, ki vrne spremenjen search objekt.
			/*
			 * using (var fromSrc = PathSearch.FromPoint(world, locomotor, self, target, source, check).WithIgnoredActor(ignoreActor))
			 * using (var fromDest = PathSearch.FromPoint(world, locomotor, self, source, target, check).WithIgnoredActor(ignoreActor).Reverse())
			 *	pb = FindBidiPath(fromSrc, fromDest);
			*/
			using (var search = PathSearch.FromPoints(world, locomotor, self, new[] { source }, target, check).WithIgnoredActor(ignoreActor))
					pb = FindPath(search);

			return pb;
		}

		public List<CPos> FindUnitPathWHCA(CPos source, CPos target, Actor self, Actor ignoreActor, BlockedByActor check, int wSteps)
		{
			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			var locomotor = ((Mobile)self.OccupiesSpace).Locomotor;

			if (!cached)
			{
				domainIndex = world.WorldActor.TraitOrDefault<DomainIndex>();
				cached = true;
			}

			// If a water-land transition is required, bail early
			if (domainIndex != null && !domainIndex.IsPassable(source, target, locomotor))
				return Enumerable.Repeat(source, wSteps).ToList();

			var distance = source - target;
			var canMoveFreely = locomotor.CanMoveFreelyInto(self, target, check, null);

			// If target is neighbouring cell. Do not check if target is waiting in current cell (= distance is 0)
			/*
			if (distance.LengthSquared != 0 && distance.LengthSquared < 3 && !canMoveFreely)
				return Enumerable.Repeat(source, wSteps).ToList(); // return new List<CPos> { };
			*/
			if (distance.LengthSquared != 0 && source.Layer == target.Layer && distance.LengthSquared < 3 && canMoveFreely)
				return Enumerable.Repeat(target, wSteps).ToList();

			List<CPos> pb;

			using (var search = PathSearch.FromPoint(world, locomotor, self, source, target, check, wSteps).WithIgnoredActor(ignoreActor))
			{
				search.Graph.IgnoreActor = self;
				pb = FindPathWHCA(search, target, wSteps);
			}

			return pb;
		}

		public List<CPos> FindUnitPathToRange(CPos source, SubCell srcSub, WPos target, WDist range, Actor self, BlockedByActor check, int wLimit)
		{
			if (!cached)
			{
				domainIndex = world.WorldActor.TraitOrDefault<DomainIndex>();
				cached = true;
			}

			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			var mobile = (Mobile)self.OccupiesSpace;
			var locomotor = mobile.Locomotor;

			var targetCell = world.Map.CellContaining(target);

			// Correct for SubCell offset
			target -= world.Map.Grid.OffsetOfSubCell(srcSub);

			// Select only the tiles that are within range from the requested SubCell SLO: tiles == Cells zato delijo z 1024 + 1
			// This assumes that the SubCell does not change during the path traversal
			var tilesInRange = world.Map.FindTilesInCircle(targetCell, range.Length / 1024 + 1)
				.Where(t => (world.Map.CenterOfCell(t) - target).LengthSquared <= range.LengthSquared
							&& mobile.Info.CanEnterCell(self.World, self, t));

			// See if there is any cell within range that does not involve a cross-domain request
			// Really, we only need to check the circle perimeter, but it's not clear that would be a performance win
			if (domainIndex != null)
			{
				tilesInRange = new List<CPos>(tilesInRange.Where(t => domainIndex.IsPassable(source, t, locomotor)));
				if (!tilesInRange.Any())
					return EmptyPath;
			}

			/* using (var fromSrc = PathSearch.FromPoints(world, locomotor, self, tilesInRange, source, check))
			 * using (var fromDest = PathSearch.FromPoint(world, locomotor, self, source, targetCell, check).Reverse())
			*/
			using (var search = PathSearch.FromPoint(world, locomotor, self, source, targetCell, check, wLimit))
				return FindPathWHCA(search, targetCell, wLimit);
		}

		public List<CPos> FindPath(IPathSearch search)
		{
			List<CPos> path = null;

			while (search.CanExpand)
			{
				var p = search.Expand();
				if (search.IsTarget(p))
				{
					path = MakePath(search.Graph, p);
					break;
				}
			}

			search.Graph.Dispose();

			if (path != null)
				return path;

			// no path exists
			return EmptyPath;
		}

		public List<CPos> FindPathWHCA(IPathSearch search, CPos goal, int wLimit)
		{
			List<CPos> path = null;

			while (search.CanExpand)
			{
				var (p, t) = search.ExpandWHCA(goal, wLimit);
				if (t == wLimit)
				{
					path = MakePathWHCA(search.Graph, p, t);
					break;
				}
			}

			search.Graph.Dispose();

			if (path != null)
				return path;

			// no path exists
			return EmptyPath;
		}

		public static bool ResumeRRA(IPathSearch search, CPos n)
		{
			while (search.CanExpand)
			{
				var p = search.ExpandRRA(x => IsNodeN(x, n));
				if (p.X == n.X & p.Y == n.Y)
					return true;
			}

			return false;
		}

		private static bool IsNodeN(CPos x, CPos n)
		{
			return (x.X == n.X && x.Y == n.Y);
		}

		// Searches from both ends toward each other. This is used to prevent blockings in case we find
		// units in the middle of the path that prevent us to continue.
		public List<CPos> FindBidiPath(IPathSearch fromSrc, IPathSearch fromDest)
		{
			List<CPos> path = null;

			while (fromSrc.CanExpand && fromDest.CanExpand)
			{
				// make some progress on the first search
				var p = fromSrc.Expand();
				if (fromDest.Graph[p].Status == CellStatus.Closed &&
					fromDest.Graph[p].CostSoFar < int.MaxValue)
				{
					path = MakeBidiPath(fromSrc, fromDest, p);
					break;
				}

				// make some progress on the second search
				var q = fromDest.Expand();
				if (fromSrc.Graph[q].Status == CellStatus.Closed &&
					fromSrc.Graph[q].CostSoFar < int.MaxValue)
				{
					path = MakeBidiPath(fromSrc, fromDest, q);
					break;
				}
			}

			fromSrc.Graph.Dispose();
			fromDest.Graph.Dispose();

			if (path != null)
				return path;

			return EmptyPath;
		}

		// Build the path from the destination. When we find a node that has the same previous
		// position than itself, that node is the source node.
		static List<CPos> MakePath(IGraph<CellInfo> cellInfos, CPos destination)
		{
			var ret = new List<CPos>();
			var currentNode = destination;

			while (cellInfos[currentNode].PreviousPos != currentNode)
			{
				ret.Add(currentNode);
				if (!cellInfos[currentNode].PreviousPos.HasValue)
					return ret;
				currentNode = cellInfos[currentNode].PreviousPos.Value;
			}

			ret.Add(currentNode);
			return ret;
		}

		static List<CPos> MakePathWHCA(IGraph<CellInfo> cellInfos, CPos destination, int timestep)
		{
			var ret = new List<CPos>();
			CPos currentNode = destination;
			var currentTimestep = timestep;

			while (currentTimestep > 0)
			{
				ret.Add(currentNode);
				if (!cellInfos[(currentNode, currentTimestep)].PreviousPos.HasValue)
					return ret;
				currentNode = cellInfos[(currentNode, currentTimestep)].PreviousPos.Value;
				currentTimestep -= 1;
			}

			return ret;
		}

		static List<CPos> MakeBidiPath(IPathSearch a, IPathSearch b, CPos confluenceNode)
		{
			var ca = a.Graph;
			var cb = b.Graph;

			var ret = new List<CPos>();

			var q = confluenceNode;
			while (ca[q].PreviousPos != q)
			{
				ret.Add(q);
				q = ca[q].PreviousPos.Value;
			}

			ret.Add(q);

			ret.Reverse();

			q = confluenceNode;
			while (cb[q].PreviousPos != q)
			{
				q = cb[q].PreviousPos.Value;
				ret.Add(q);
			}

			return ret;
		}
	}
}
