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
using System.Diagnostics;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common.Pathfinder;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public class Move : Activity
	{
		static readonly List<CPos> NoPath = new List<CPos>();

		readonly Mobile mobile;
		readonly WDist nearEnough;
		readonly Func<int, BlockedByActor, List<CPos>> getPath;
		readonly Actor ignoreActor;
		readonly Color? targetLineColor;
		readonly SpaceTimeReservation spaceTimeReservation;

		static readonly BlockedByActor[] PathSearchOrder =
		{
			BlockedByActor.Immovable,
			BlockedByActor.None
		};

		// List<CPos> path; TEMPORARY PROPERTY FOR DEBUG PORPUSES
		private List<CPos> fpath;
#pragma warning disable IDE1006 // Naming Styles
#pragma warning disable SA1300 // Element should begin with upper-case letter
		public List<CPos> path
#pragma warning restore SA1300 // Element should begin with upper-case letter
#pragma warning restore IDE1006 // Naming Styles
		{
			get { return fpath; }

			set { fpath = value; }
		}

		CPos? destination;

		// For counting moves until mobile.W/2
		int wCounter;

		// For preventing cycling Turn activity on reWindowing (= reset) tick
		bool turnQueued;

		// For preventing cycling Wait activity on reWindowing (= reset) tick
		bool waitQueued = false;

		// For dealing with blockers
		// bool hasWaited;
		// int waitTicksRemaining;

		// To work around queued activity issues while minimizing changes to legacy behaviour
		bool evaluateNearestMovableCell;

		// Scriptable move order
		// Ignores lane bias and nearby units
		public Move(Actor self, CPos destination, Color? targetLineColor = null)
		{
			spaceTimeReservation = self.Owner.PlayerActor.Trait<SpaceTimeReservation>();

			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			mobile = (Mobile)self.OccupiesSpace;

			turnQueued = false;

			getPath = (wLimit, check) =>
			{
				if (!this.destination.HasValue)
					return Enumerable.Repeat(mobile.FromCell, wLimit).ToList();

				List<CPos> path;

				using (var search =
					PathSearch.FromPoint(self.World, mobile.Locomotor, self, mobile.ToCell, destination, check, wLimit)
					.WithoutLaneBias())
				{
					search.Graph.IgnoreActor = self;
					path = mobile.Pathfinder.FindPathWHCA(search, destination, wLimit);
				}

				return path;
			};

			ignoreActor = self;
			this.destination = destination;
			this.targetLineColor = targetLineColor;
			nearEnough = WDist.Zero;
		}

		public Move(Actor self, CPos destination, WDist nearEnough, Actor ignoreActor = null, bool evaluateNearestMovableCell = false,
			Color? targetLineColor = null)
		{
			this.ignoreActor = self;
			spaceTimeReservation = self.Owner.PlayerActor.Trait<SpaceTimeReservation>();

			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			mobile = (Mobile)self.OccupiesSpace;

			turnQueued = false;

			getPath = (wLimit, check) =>
			{
				if (!this.destination.HasValue)
					return Enumerable.Repeat(mobile.FromCell, wLimit).ToList();
				return mobile.Pathfinder.FindUnitPathWHCA(mobile.ToCell, this.destination.Value, self, this.ignoreActor, check, wLimit);
			};

			// Note: Will be recalculated from OnFirstRun if evaluateNearestMovableCell is true
			this.destination = destination;

			this.nearEnough = nearEnough;
			this.evaluateNearestMovableCell = evaluateNearestMovableCell;
			this.targetLineColor = targetLineColor;
		}

		public Move(Actor self, CPos destination, SubCell subCell, WDist nearEnough, Color? targetLineColor = null)
		{
			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			mobile = (Mobile)self.OccupiesSpace;

			turnQueued = false;

			getPath = (wLimit, check) => mobile.Pathfinder.FindUnitPathToRange(
				mobile.FromCell, subCell, self.World.Map.CenterOfSubCell(destination, subCell), nearEnough, self, check, wLimit);

			this.destination = destination;
			this.nearEnough = nearEnough;
			this.targetLineColor = targetLineColor;
		}

		public Move(Actor self, Target target, WDist range, Color? targetLineColor = null)
		{
			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			mobile = (Mobile)self.OccupiesSpace;

			turnQueued = false;

			getPath = (wLimit, check) =>
			{
				if (!target.IsValidFor(self))
					return Enumerable.Repeat(mobile.FromCell, wLimit).ToList();

				return mobile.Pathfinder.FindUnitPathToRange(
					mobile.ToCell, mobile.ToSubCell, target.CenterPosition, range, self, check, wLimit);
			};

			destination = null;
			nearEnough = range;
			this.targetLineColor = targetLineColor;
		}

		public Move(Actor self, Func<int, BlockedByActor, List<CPos>> getPath, CPos lastVisibleTargetLocation, Color? targetLineColor = null)
		{
			spaceTimeReservation = self.Owner.PlayerActor.Trait<SpaceTimeReservation>();

			// PERF: Because we can be sure that OccupiesSpace is Mobile here, we can save some performance by avoiding querying for the trait.
			mobile = (Mobile)self.OccupiesSpace;

			turnQueued = false;

			this.getPath = getPath;
			ignoreActor = self;
			destination = lastVisibleTargetLocation;
			nearEnough = WDist.Zero;
			this.targetLineColor = targetLineColor;
		}

		static int HashList<T>(List<T> xs)
		{
			var hash = 0;
			var n = 0;
			foreach (var x in xs)
				hash += n++ * x.GetHashCode();

			return hash;
		}

		List<CPos> EvalPath(World world, BlockedByActor check)
		{
			// var tmp = world.WorldTick;
			// var tmp2 = spaceTimeReservation.Check(1, 1, tmp);
			/*
			Stopwatch stopWatch = new Stopwatch(); // SLO
			stopWatch.Start();
			*/
			var path = getPath(mobile.W - wCounter, check); // .TakeWhile(a => a != mobile.ToCell).ToList();
			/*
			stopWatch.Stop();
			// Get the elapsed time as a TimeSpan value.
			long ms = stopWatch.ElapsedMilliseconds;

			// Format and display the TimeSpan value.
			Console.WriteLine("FindUnitPath " + ms.ToString());
			*/
			mobile.PathHash = HashList(path);
			return path;
		}

		protected override void OnFirstRun(Actor self)
		{
			wCounter = -1;

			if (evaluateNearestMovableCell && destination.HasValue)
			{
				var movableDestination = mobile.NearestMoveableCell(destination.Value);
				destination = mobile.CanEnterCell(movableDestination, check: BlockedByActor.Immovable) ? movableDestination : (CPos?)null;
			}

			if (destination.HasValue)
				mobile.RRAsearch = PathSearch.InitialiseRRA(self.World, mobile.Locomotor, self, mobile.ToCell, destination.Value, BlockedByActor.Immovable);
		}

		protected void InitiliseWindowedSearch(Actor self)
		{
			wCounter = 0;

			// TODO: Change this to BlockedByActor.Stationary after improving the local avoidance behaviour
			foreach (var check in PathSearchOrder)
			{
				path = EvalPath(self.World, check);
				if (path.Count > 0)
					return;
			}
		}

		public override bool Tick(Actor self)
		{
			// force reWindow for better scynchronization
			var reWindow = self.World.WorldTick % (mobile.W * mobile.ResetSpeed) == 0;

			if (wCounter == -1 || wCounter >= mobile.W / 2 || reWindow)
				InitiliseWindowedSearch(self);

			mobile.TurnToMove = false;

			if (IsCanceling && mobile.CanStayInCell(mobile.ToCell))
			{
				path?.Clear();
				return true;
			}

			if (mobile.IsTraitDisabled || mobile.IsTraitPaused)
				return false;

			if (path.Count == 0)
			{
				// Agent should never stop moving
				throw new Exception("Path is empty.");

				// destination = mobile.ToCell;
				// return true;
			}

			// destination = path[0];
			var nextCell = PopPath(self);
			if (nextCell == null)
				return false;

			// We need to turn to face the target.
			var firstFacing = self.World.Map.FacingBetween(mobile.FromCell, nextCell.Value.Cell, mobile.Facing);
			if (firstFacing != mobile.Facing)
			{
				path.Add(nextCell.Value.Cell);
				QueueChild(new Turn(self, firstFacing));
				mobile.TurnToMove = true;
				if (turnQueued) // avoid cycling Turn and Move actions due to no single tick delay in Activity.TickOuter()
					ChildActivity.Delay = true;
				turnQueued = true;
				return false;
			}

			turnQueued = false;

			// Queue wait action if we need to stand stil. za 1024 wpos tickov
			if (mobile.FromCell == nextCell.Value.Cell)
			{
				var waitTicks = 1024 / mobile.MovementSpeedForCell(self, mobile.FromCell);
				Func<bool> f = () =>
				{
					waitTicks -= 1;
					if (waitTicks <= 0)
						return true;

					// force reWindowing for move
					if (mobile != null && self.World.WorldTick % (mobile.W * mobile.ResetSpeed) == 0)
						return true;

					return false;
				};
				QueueChild(new WaitFor(f));

				if (waitQueued)
					ChildActivity.Delay = true;
				waitQueued = true;

				wCounter += 1;
				return false;
			}

			waitQueued = false;

			// Queue MoveFirstHalf
			mobile.SetLocation(mobile.FromCell, mobile.FromSubCell, nextCell.Value.Cell, nextCell.Value.SubCell);

			var map = self.World.Map;
			var from = (mobile.FromCell.Layer == 0 ? map.CenterOfCell(mobile.FromCell) :
				self.World.GetCustomMovementLayers()[mobile.FromCell.Layer].CenterOfCell(mobile.FromCell)) +
				map.Grid.OffsetOfSubCell(mobile.FromSubCell);

			var to = Util.BetweenCells(self.World, mobile.FromCell, mobile.ToCell) +
				(map.Grid.OffsetOfSubCell(mobile.FromSubCell) + map.Grid.OffsetOfSubCell(mobile.ToSubCell)) / 2;

			QueueChild(new MoveFirstHalf(this, from, to, mobile.Facing, mobile.Facing, 0));
			return false;
		}

		(CPos Cell, SubCell SubCell)? PopPath(Actor self)
		{
			if (path.Count == 0)
				return null;

			var nextCell = path[path.Count - 1];

			// Something else might have moved us, so the path is no longer valid.
			if (!Util.AreAdjacentCells(mobile.ToCell, nextCell))
			{
				// path = EvalPath(self.World, BlockedByActor.Immovable);
				// return null;
				nextCell = Repath(self, BlockedByActor.Immovable);
			}

			var containsTemporaryBlocker = WorldUtils.ContainsTemporaryBlocker(self.World, nextCell, self);

			// Next cell in the move is blocked by another actor
			if (containsTemporaryBlocker || !mobile.CanEnterCell(nextCell, ignoreActor))
			{
				// Are we close enough?
				var cellRange = nearEnough.Length / 1024;
				if (!containsTemporaryBlocker && (mobile.ToCell - destination.Value).LengthSquared <= cellRange * cellRange && mobile.CanStayInCell(mobile.ToCell))
				{
					// Apply some simple checks to avoid giving up in cases where we can be confident that
					// nudging/waiting/repathing should produce better results.

					// Avoid fighting over the destination cell
					if (path.Count < 2)
					{
						// path.Clear();
						ChangeDestination(self);
						nextCell = ChangeDestination(self);
						path.RemoveAt(path.Count - 1);
						return (nextCell, mobile.GetAvailableSubCell(nextCell, mobile.FromSubCell, ignoreActor));

						// return null;
					}

					// We can reasonably assume that the blocker is friendly and has a similar locomotor type.
					// If there is a free cell next to the blocker that is a similar or closer distance to the
					// destination then we can probably nudge or path around it.
					var blockerDistSq = (nextCell - destination.Value).LengthSquared;
					var nudgeOrRepath = CVec.Directions
						.Select(d => nextCell + d)
						.Any(c => c != self.Location && (c - destination.Value).LengthSquared <= blockerDistSq && mobile.CanEnterCell(c, ignoreActor));

					if (!nudgeOrRepath)
					{
						// path.Clear();
						nextCell = ChangeDestination(self);
						path.RemoveAt(path.Count - 1);
						return (nextCell, mobile.GetAvailableSubCell(nextCell, mobile.FromSubCell, ignoreActor));

						// return null;
					}
				}

				// There is no point in waiting for the other actor to move if it is incapable of moving.
				if (!mobile.CanEnterCell(nextCell, ignoreActor, BlockedByActor.Immovable))
				{
					// path = EvalPath(self.World, BlockedByActor.Immovable);
					// return null;
					nextCell = Repath(self, BlockedByActor.Immovable);

					// keep found path if we can move to next cell
					// mobile.Locomotor.CanMoveFreelyIntoWHCA(self, nextCell, BlockedByActor.All, ignoreActor);
					if (mobile.Locomotor.CanMoveFreelyIntoWHCA(self, nextCell, BlockedByActor.All, ignoreActor))
					{
						path.RemoveAt(path.Count - 1);
						return (nextCell, mobile.GetAvailableSubCell(nextCell, mobile.FromSubCell, ignoreActor));
					}
				}

				/*
				// See if they will move
				self.NotifyBlocker(nextCell);

				// Wait a bit to see if they leave
				if (!hasWaited)
				{
					waitTicksRemaining = mobile.Info.LocomotorInfo.WaitAverage;
					hasWaited = true;
					return null;
				}

				if (--waitTicksRemaining >= 0)
					return null;

				hasWaited = false;

				// If the blocking actors are already leaving, wait a little longer instead of repathing
				if (CellIsEvacuating(self, nextCell))
					return null;

				// Calculate a new path
				mobile.RemoveInfluence();
				var newPath = EvalPath(self.World, BlockedByActor.All);
				mobile.AddInfluence();

				if (newPath.Count != 0)
				{
					path = newPath;
					var newCell = path[path.Count - 1];
					path.RemoveAt(path.Count - 1);

					return (newCell, mobile.GetAvailableSubCell(nextCell, mobile.FromSubCell, ignoreActor));
				}

				else if (mobile.IsBlocking)
				{
					// If there is no way around the blocker and blocker will not move and we are blocking others, back up to let others pass.
					var newCell = mobile.GetAdjacentCell(nextCell);
					if (newCell != null)
					{
						if ((nextCell - newCell).Value.LengthSquared > 2)
							path.Add(mobile.ToCell);

						return (newCell.Value, mobile.GetAvailableSubCell(newCell.Value, mobile.FromSubCell, ignoreActor));
					}
				}

				return null;
				*/

				// Repath is required, NOTE: WE REPATH AROUND ALL AGENTS, THIS IS LAST RESORT ACTION
				nextCell = Repath(self, BlockedByActor.All);
			}

			// hasWaited = false;
			path.RemoveAt(path.Count - 1);
			return (nextCell, mobile.GetAvailableSubCell(nextCell, mobile.FromSubCell, ignoreActor));
		}

		private CPos Repath(Actor self, BlockedByActor check)
		{
			// Calculate a new path
			mobile.RemoveInfluence();
			path = EvalPath(self.World, check);
			mobile.AddInfluence();
			return path[path.Count - 1];
		}

		private CPos ChangeDestination(Actor self)
		{
			// change destination or queue new move with new goal
			// self.QueueActivity(mobile.MoveTo(mobile.ToCell, 0, self, true));
			destination = mobile.ToCell;
			OnFirstRun(self);

			// wCounter = mobile.W; // to make sure search is reinitialised
			// path.Clear();
			InitiliseWindowedSearch(self);
			return path[path.Count - 1];
		}

		protected override void OnLastRun(Actor self)
		{
			if (destination.HasValue)
				mobile.RRAsearch.Graph.Dispose();
			path = null;
		}

		bool CellIsEvacuating(Actor self, CPos cell)
		{
			foreach (var actor in self.World.ActorMap.GetActorsAt(cell))
			{
				var move = actor.TraitOrDefault<Mobile>();
				if (move == null || !move.IsTraitEnabled() || !move.IsLeaving())
					return false;
			}

			return true;
		}

		public override void Cancel(Actor self, bool keepQueue = false)
		{
			Cancel(self, keepQueue, false);
		}

		public void Cancel(Actor self, bool keepQueue, bool forceClearPath)
		{
			// We need to clear the path here in order to prevent MovePart queueing new instances of itself
			// when the unit is making a turn.
			if (path != null && (forceClearPath || mobile.CanStayInCell(mobile.ToCell)))
				path.Clear();

			base.Cancel(self, keepQueue);
		}

		public override IEnumerable<Target> GetTargets(Actor self)
		{
			if (path != null)
				return Enumerable.Reverse(path).Select(c => Target.FromCell(self.World, c));
			if (destination != null)
				return new Target[] { Target.FromCell(self.World, destination.Value) };
			return Target.None;
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			// destination might be initialized with null, but will be set in a subsequent tick
			if (targetLineColor != null && destination != null)
				yield return new TargetLineNode(Target.FromCell(self.World, destination.Value), targetLineColor.Value);
		}

		abstract class MovePart : Activity
		{
			protected readonly Move Move;
			protected readonly WPos From, To;
			protected readonly WAngle FromFacing, ToFacing;
			protected readonly bool EnableArc;
			protected readonly WPos ArcCenter;
			protected readonly int ArcFromLength;
			protected readonly WAngle ArcFromAngle;
			protected readonly int ArcToLength;
			protected readonly WAngle ArcToAngle;

			protected readonly int MoveFractionTotal;
			protected int moveFraction;

			public MovePart(Move move, WPos from, WPos to, WAngle fromFacing, WAngle toFacing, int startingFraction)
			{
				Move = move;
				From = from;
				To = to;
				FromFacing = fromFacing;
				ToFacing = toFacing;
				moveFraction = startingFraction;
				MoveFractionTotal = (to - from).Length;
				IsInterruptible = false; // See comments in Move.Cancel()

				// Calculate an elliptical arc that joins from and to
				var delta = (fromFacing - toFacing).Angle;
				if (delta != 0 && delta != 512)
				{
					// The center of rotation is where the normal vectors cross
					var u = new WVec(1024, 0, 0).Rotate(WRot.FromYaw(fromFacing));
					var v = new WVec(1024, 0, 0).Rotate(WRot.FromYaw(toFacing));

					// Make sure that u and v aren't parallel, which may happen due to rounding
					// in WVec.Rotate if delta is close but not necessarily equal to 0 or 512
					if (v.X * u.Y != v.Y * u.X)
					{
						var w = from - to;
						var s = (v.Y * w.X - v.X * w.Y) * 1024 / (v.X * u.Y - v.Y * u.X);
						var x = from.X + s * u.X / 1024;
						var y = from.Y + s * u.Y / 1024;

						ArcCenter = new WPos(x, y, 0);
						ArcFromLength = (ArcCenter - from).HorizontalLength;
						ArcFromAngle = (ArcCenter - from).Yaw;
						ArcToLength = (ArcCenter - to).HorizontalLength;
						ArcToAngle = (ArcCenter - to).Yaw;
						EnableArc = true;
					}
				}
			}

			public override bool Tick(Actor self)
			{
				var ret = InnerTick(self, Move.mobile);

				if (moveFraction > MoveFractionTotal)
					moveFraction = MoveFractionTotal;

				UpdateCenterLocation(self, Move.mobile);

				// force reWindow for better scynchronization
				var reWindow = self.World.WorldTick % (Move.mobile.W * Move.mobile.ResetSpeed) == 0;
				if (reWindow)
					Move.InitiliseWindowedSearch(self);

				if (ret == this)
					return false;

				Queue(ret);
				return true;
			}

			Activity InnerTick(Actor self, Mobile mobile)
			{
				moveFraction += mobile.MovementSpeedForCell(self, mobile.ToCell);
				if (moveFraction <= MoveFractionTotal)
					return this;

				return OnComplete(self, mobile, Move);
			}

			void UpdateCenterLocation(Actor self, Mobile mobile)
			{
				// Avoid division through zero
				if (MoveFractionTotal != 0)
				{
					WPos pos;
					if (EnableArc)
					{
						var angle = WAngle.Lerp(ArcFromAngle, ArcToAngle, moveFraction, MoveFractionTotal);
						var length = int2.Lerp(ArcFromLength, ArcToLength, moveFraction, MoveFractionTotal);
						var height = int2.Lerp(From.Z, To.Z, moveFraction, MoveFractionTotal);
						pos = ArcCenter + new WVec(0, length, height).Rotate(WRot.FromYaw(angle));
					}
					else
						pos = WPos.Lerp(From, To, moveFraction, MoveFractionTotal);

					if (self.Location.Layer == 0)
						pos -= new WVec(WDist.Zero, WDist.Zero, self.World.Map.DistanceAboveTerrain(pos));

					mobile.SetVisualPosition(self, pos);
				}
				else
					mobile.SetVisualPosition(self, To);

				if (moveFraction >= MoveFractionTotal)
					mobile.Facing = ToFacing;
				else
					mobile.Facing = WAngle.Lerp(FromFacing, ToFacing, moveFraction, MoveFractionTotal);
			}

			protected abstract MovePart OnComplete(Actor self, Mobile mobile, Move parent);

			public override IEnumerable<Target> GetTargets(Actor self)
			{
				return Move.GetTargets(self);
			}
		}

		class MoveFirstHalf : MovePart
		{
			public MoveFirstHalf(Move move, WPos from, WPos to, WAngle fromFacing, WAngle toFacing, int startingFraction)
				: base(move, from, to, fromFacing, toFacing, startingFraction) { }

			static bool IsTurn(Mobile mobile, CPos nextCell, Map map)
			{
				// Some actors with a limited number of sprite facings should never move along curved trajectories.
				if (mobile.Info.AlwaysTurnInPlace)
					return false;

				// Tight U-turns should be done in place instead of making silly looking loops.
				var nextFacing = map.FacingBetween(nextCell, mobile.ToCell, mobile.Facing);
				var currentFacing = map.FacingBetween(mobile.ToCell, mobile.FromCell, mobile.Facing);
				var delta = (nextFacing - currentFacing).Angle;
				return delta != 0 && (delta < 384 || delta > 640);
			}

			protected override MovePart OnComplete(Actor self, Mobile mobile, Move parent)
			{
				Move.wCounter += 1;

				var map = self.World.Map;
				var fromSubcellOffset = map.Grid.OffsetOfSubCell(mobile.FromSubCell);
				var toSubcellOffset = map.Grid.OffsetOfSubCell(mobile.ToSubCell);

				var nextCell = parent.PopPath(self);
				if (nextCell != null)
				{
					if (mobile.ToCell != mobile.FromCell)
					{
					}

					if (!mobile.IsTraitPaused && !mobile.IsTraitDisabled && IsTurn(mobile, nextCell.Value.Cell, map)) 
					{
						var nextSubcellOffset = map.Grid.OffsetOfSubCell(nextCell.Value.SubCell);
						var ret = new MoveFirstHalf(
							Move,
							Util.BetweenCells(self.World, mobile.FromCell, mobile.ToCell) + (fromSubcellOffset + toSubcellOffset) / 2,
							Util.BetweenCells(self.World, mobile.ToCell, nextCell.Value.Cell) + (toSubcellOffset + nextSubcellOffset) / 2,
							mobile.Facing,
							map.FacingBetween(mobile.ToCell, nextCell.Value.Cell, mobile.Facing),
							moveFraction - MoveFractionTotal);

						mobile.FinishedMoving(self);
						mobile.SetLocation(mobile.ToCell, mobile.ToSubCell, nextCell.Value.Cell, nextCell.Value.SubCell);
						return ret;
					}

					parent.path.Add(nextCell.Value.Cell);
				}

				var toPos = mobile.ToCell.Layer == 0 ? map.CenterOfCell(mobile.ToCell) :
					self.World.GetCustomMovementLayers()[mobile.ToCell.Layer].CenterOfCell(mobile.ToCell);

				var ret2 = new MoveSecondHalf(
					Move,
					Util.BetweenCells(self.World, mobile.FromCell, mobile.ToCell) + (fromSubcellOffset + toSubcellOffset) / 2,
					toPos + toSubcellOffset,
					mobile.Facing,
					mobile.Facing,
					moveFraction - MoveFractionTotal);

				mobile.EnteringCell(self);
				mobile.SetLocation(mobile.ToCell, mobile.ToSubCell, mobile.ToCell, mobile.ToSubCell);
				return ret2;
			}
		}

		class MoveSecondHalf : MovePart
		{
			public MoveSecondHalf(Move move, WPos from, WPos to, WAngle fromFacing, WAngle toFacing, int startingFraction)
				: base(move, from, to, fromFacing, toFacing, startingFraction) { }

			protected override MovePart OnComplete(Actor self, Mobile mobile, Move parent)
			{
				mobile.SetPosition(self, mobile.ToCell);
				return null;
			}
		}
	}
}
