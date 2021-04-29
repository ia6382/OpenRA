using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenRA.Graphics;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Attach this to the player actor for path reservation during WHCA* cooperative search.")]
	public class SpaceTimeReservationInfo : TraitInfo
	{
		[Desc("Modulus operator - how many ticks of time we remember before we overwrite reservations.")]
		public readonly int TimeLength = 4999;

		public override object Create(ActorInitializer init) { return new SpaceTimeReservation(init.Self.Owner, this); } // SLO: se this ce potrebuje fields iz Info classa.
	}

	public class SpaceTimeReservation
	{
		private SparseMatrix<uint> reservationTable;

		public readonly SpaceTimeReservationInfo Info;
		public readonly Player Owner;

		public SpaceTimeReservation(Player owner, SpaceTimeReservationInfo info)
		{
			Info = info;
			Owner = owner;

			if (!owner.Spectating)
				reservationTable = new SparseMatrix<uint>();
			else
				reservationTable = null;
		}

		public void Reserve(int x, int y, int t, Actor agent)
		{
			var wrappedT = t % Info.TimeLength;
			reservationTable[x, y, wrappedT] = agent.ActorID;
		}

		public void Free(int x, int y, int t, Actor agent)
		{
			var wrappedT = t % Info.TimeLength;
			reservationTable.RemoveKey(x, y, wrappedT);
		}

		public bool Check(int x, int y, int t, Actor agent)
		{
			var wrappedT = t % Info.TimeLength;
			return reservationTable.ContainsKey(x, y, wrappedT);
		}
	}
}
