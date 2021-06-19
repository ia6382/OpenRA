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
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Pathfinder
{
	/// <summary>
	/// Represents a graph with nodes and edges
	/// </summary>
	/// <typeparam name="T1">The type of node used in the graph (value)</typeparam>
	public interface IGraph<T1> : IDisposable
	{
		/// <summary>
		/// Gets all the Connections for a given node in the graph
		/// </summary>
		List<GraphConnection> GetConnections(CPos position);
		List<GraphConnection> GetConnectionsWHCA(CPos position);

		/// <summary>
		/// Retrieves an object given a node in the graph
		/// </summary>
		T1 this[CPos pos] { get; set; }

		T1 this[(CPos, int) pos] { get; set; }

		Func<CPos, bool> CustomBlock { get; set; }

		Func<CPos, int> CustomCost { get; set; }

		int LaneBias { get; set; }

		bool InReverse { get; set; }

		Actor IgnoreActor { get; set; }

		World World { get; }

		Actor Actor { get; }
	}

	public struct GraphConnection
	{
		public static readonly CostComparer ConnectionCostComparer = CostComparer.Instance;

		public sealed class CostComparer : IComparer<GraphConnection>
		{
			public static readonly CostComparer Instance = new CostComparer();
			CostComparer() { }
			public int Compare(GraphConnection x, GraphConnection y)
			{
				return x.Cost.CompareTo(y.Cost);
			}
		}

		public readonly CPos Destination;
		public readonly int Cost;
		public readonly int Timestep;

		public GraphConnection(CPos destination, int cost)
		{
			Destination = destination;
			Cost = cost;
			Timestep = 0;
		}

		public GraphConnection(CPos destination, int cost, int timestep)
		{
			Destination = destination;
			Cost = cost;
			Timestep = timestep;
		}
	}

	class PathGraph : IGraph<CellInfo>
	{
		public const int CostForInvalidCell = int.MaxValue;

		public Actor Actor { get; set; }
		public World World { get; set; }
		public Func<CPos, bool> CustomBlock { get; set; }
		public Func<CPos, int> CustomCost { get; set; }
		public int LaneBias { get; set; }
		public bool InReverse { get; set; }
		public Actor IgnoreActor { get; set; }

		protected BlockedByActor checkConditions;
		protected Locomotor locomotor;
		protected CellInfoLayerPool.PooledCellInfoLayer pooledLayer;
		protected bool checkTerrainHeight;
		protected CellLayer<CellInfo> groundInfo;

		protected Dictionary<byte, (ICustomMovementLayer Layer, CellLayer<CellInfo> Info)> customLayerInfo =
			new Dictionary<byte, (ICustomMovementLayer, CellLayer<CellInfo>)>();

		public PathGraph(CellInfoLayerPool layerPool, Locomotor locomotor, Actor actor, World world, BlockedByActor check)
		{
			pooledLayer = layerPool.Get();
			groundInfo = pooledLayer.GetLayer();
			var locomotorInfo = locomotor.Info;
			this.locomotor = locomotor;
			var layers = world.GetCustomMovementLayers().Values
				.Where(cml => cml.EnabledForActor(actor.Info, locomotorInfo));

			foreach (var cml in layers)
				customLayerInfo[cml.Index] = (cml, pooledLayer.GetLayer());

			World = world;
			Actor = actor;
			LaneBias = 1;
			checkConditions = check;
			checkTerrainHeight = world.Map.Grid.MaximumTerrainHeight > 0;
		}

		// Sets of neighbors for each incoming direction. These exclude the neighbors which are guaranteed
		// to be reached more cheaply by a path through our parent cell which does not include the current cell.
		// For horizontal/vertical directions, the set is the three cells 'ahead'. For diagonal directions, the set
		// is the three cells ahead, plus the two cells to the side, which we cannot exclude without knowing if
		// the cell directly between them and our parent is passable.
		static readonly CVec[][] DirectedNeighbors =
		{
			new[] { new CVec(-1, -1), new CVec(0, -1), new CVec(1, -1), new CVec(-1, 0), new CVec(-1, 1), new CVec(0, 0) },
			new[] { new CVec(-1, -1), new CVec(0, -1), new CVec(1, -1), new CVec(0, 0) },
			new[] { new CVec(-1, -1), new CVec(0, -1), new CVec(1, -1), new CVec(1, 0), new CVec(1, 1), new CVec(0, 0) },
			new[] { new CVec(-1, -1), new CVec(-1, 0), new CVec(-1, 1), new CVec(0, 0) },
			new[] { new CVec(-1, -1), new CVec(-1,  0), new CVec(-1,  1), new CVec(0, -1), new CVec(0,  1), new CVec(1, -1), new CVec(1,  0), new CVec(1,  1), new CVec(0, 0) },
			new[] { new CVec(1, -1), new CVec(1, 0), new CVec(1, 1), new CVec(0, 0) },
			new[] { new CVec(-1, -1), new CVec(-1, 0), new CVec(-1, 1), new CVec(0, 1), new CVec(1, 1), new CVec(0, 0) },
			new[] { new CVec(-1, 1), new CVec(0, 1), new CVec(1, 1), new CVec(0, 0) },
			new[] { new CVec(1, -1), new CVec(1, 0), new CVec(-1, 1), new CVec(0, 1), new CVec(1, 1), new CVec(0, 0) },
		};

		public List<GraphConnection> GetConnectionsWHCA(CPos position)
		{
			return GetConnections(position, (x, y, z, w) => locomotor.CanMoveFreelyIntoWHCA(x, y, z, w));
		}

		public List<GraphConnection> GetConnections(CPos position)
		{
			return GetConnections(position, (x, y, z, w) => locomotor.CanMoveFreelyInto(x, y, z, w));
		}

		public List<GraphConnection> GetConnections(CPos position, Func<Actor, CPos, BlockedByActor, Actor, bool> canMoveInto)
		{
			var info = position.Layer == 0 ? groundInfo : customLayerInfo[position.Layer].Info;
			var previousPos = info[position].PreviousPos;
			var index = 0;
			if (previousPos.HasValue)
			{
				var dx = position.X - previousPos.Value.X;
				var dy = position.Y - previousPos.Value.Y;
				index = dy * 3 + dx + 4;
			}
			else
			{
				index = 4;
			}

			var directions = DirectedNeighbors[index];
			var validNeighbors = new List<GraphConnection>(directions.Length);
			for (var i = 0; i < directions.Length; i++)
			{
				var neighbor = position + directions[i];
				var movementCost = GetCostToNode(neighbor, directions[i], (x, y, z, w) => canMoveInto(x, y, z, w));
				if (movementCost != CostForInvalidCell)
					validNeighbors.Add(new GraphConnection(neighbor, movementCost));
			}

			if (position.Layer == 0)
			{
				foreach (var cli in customLayerInfo.Values)
				{
					var layerPosition = new CPos(position.X, position.Y, cli.Layer.Index);
					var entryCost = cli.Layer.EntryMovementCost(Actor.Info, locomotor.Info, layerPosition);
					if (entryCost != CostForInvalidCell)
						validNeighbors.Add(new GraphConnection(layerPosition, entryCost));
				}
			}
			else
			{
				var layerPosition = new CPos(position.X, position.Y, 0);
				var exitCost = customLayerInfo[position.Layer].Layer.ExitMovementCost(Actor.Info, locomotor.Info, layerPosition);
				if (exitCost != CostForInvalidCell)
					validNeighbors.Add(new GraphConnection(layerPosition, exitCost));
			}

			return validNeighbors;
		}

		int GetCostToNode(CPos destNode, CVec direction, Func<Actor, CPos, BlockedByActor, Actor, bool> canMoveInto)
		{
			var movementCost = locomotor.MovementCostToEnterCell(Actor, destNode, checkConditions, IgnoreActor, (x, y, z, w) => canMoveInto(x, y, z, w));
			if (movementCost != short.MaxValue && !(CustomBlock != null && CustomBlock(destNode)))
				return CalculateCellCost(destNode, direction, movementCost);

			return CostForInvalidCell;
		}

		int CalculateCellCost(CPos neighborCPos, CVec direction, int movementCost)
		{
			var cellCost = movementCost;

			if (direction.X * direction.Y != 0)
				cellCost = (cellCost * 34) / 24;

			if (CustomCost != null)
			{
				var customCost = CustomCost(neighborCPos);
				if (customCost == CostForInvalidCell)
					return CostForInvalidCell;

				cellCost += customCost;
			}

			// Prevent units from jumping over height discontinuities
			if (checkTerrainHeight && neighborCPos.Layer == 0)
			{
				var from = neighborCPos - direction;
				if (Math.Abs(World.Map.Height[neighborCPos] - World.Map.Height[from]) > 1)
					return CostForInvalidCell;
			}

			// Directional bonuses for smoother flow!
			if (LaneBias != 0)
			{
				var ux = neighborCPos.X + (InReverse ? 1 : 0) & 1;
				var uy = neighborCPos.Y + (InReverse ? 1 : 0) & 1;

				if ((ux == 0 && direction.Y < 0) || (ux == 1 && direction.Y > 0))
					cellCost += LaneBias;

				if ((uy == 0 && direction.X < 0) || (uy == 1 && direction.X > 0))
					cellCost += LaneBias;
			}

			return cellCost;
		}

		public CellInfo this[CPos pos]
		{
			get { return (pos.Layer == 0 ? groundInfo : customLayerInfo[pos.Layer].Info)[pos]; }
			set { (pos.Layer == 0 ? groundInfo : customLayerInfo[pos.Layer].Info)[pos] = value; }
		}

		public virtual CellInfo this[(CPos, int) pos]
		{
			get { throw new NotImplementedException(); }
			set { throw new NotImplementedException(); }
		}

		public void Dispose()
		{
			groundInfo = null;
			customLayerInfo.Clear();
			pooledLayer.Dispose();
		}
	}

	/// <summary>
	/// Represents a graph with nodes and edges in a 3D space-time (x, y, t)
	/// </summary>
	class PathGraph3D : PathGraph, IGraph<CellInfo>
	{
		new Dictionary<Tuple<int, int, int>, CellInfo> groundInfo = new Dictionary<Tuple<int, int, int>, CellInfo>();
		readonly new Dictionary<byte, (ICustomMovementLayer Layer, Dictionary<Tuple<int, int, int>, CellInfo> Info)> customLayerInfo =
			new Dictionary<byte, (ICustomMovementLayer, Dictionary<Tuple<int, int, int>, CellInfo>)>();

		public PathGraph3D(CellInfoLayerPool layerPool, Locomotor locomotor, Actor actor, World world, BlockedByActor check)
			: base(layerPool, locomotor, actor, world, check)
		{
			World = world;
			Actor = actor;
			LaneBias = 1;
			checkConditions = check;
			checkTerrainHeight = world.Map.Grid.MaximumTerrainHeight > 0;

			var locomotorInfo = locomotor.Info;
			var layers = world.GetCustomMovementLayers().Values
				.Where(cml => cml.EnabledForActor(actor.Info, locomotorInfo));

			foreach (var cml in layers)
			{
				customLayerInfo[cml.Index] = (cml, new Dictionary<Tuple<int, int, int>, CellInfo>());
			}
		}

		public new CellInfo this[(CPos, int) pos]
		{
			get
			{
				var key = new Tuple<int, int, int>(pos.Item1.X, pos.Item1.Y, pos.Item2);
				if (groundInfo.ContainsKey(key))
				{
					return (pos.Item1.Layer == 0 ? groundInfo : customLayerInfo[pos.Item1.Layer].Info)[key];
				}
				else
				{
					groundInfo[key] = new CellInfo(int.MaxValue, int.MaxValue, pos.Item1, CellStatus.Unvisited);
					return (pos.Item1.Layer == 0 ? groundInfo : customLayerInfo[pos.Item1.Layer].Info)[key];
				}
			}
			set
			{
				var key = new Tuple<int, int, int>(pos.Item1.X, pos.Item1.Y, pos.Item2);
				(pos.Item1.Layer == 0 ? groundInfo : customLayerInfo[pos.Item1.Layer].Info)[key] = value;
			}
		}

		public new void Dispose()
		{
			base.Dispose();
			groundInfo.Clear();
			customLayerInfo.Clear();
		}
	}
}
