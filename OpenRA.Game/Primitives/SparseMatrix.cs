using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OpenRA.Primitives
{
	public class SparseMatrix<T>
	{
		private Dictionary<Tuple<int, int, int>, T> data = new Dictionary<Tuple<int, int, int>, T>();

		public T this[int a, int b, int c]
		{
			get
			{
				var key = new Tuple<int, int, int>(a, b, c);
				return data[key];
			}

			set
			{
				var key = new Tuple<int, int, int>(a, b, c);
				data[key] = value;
			}
		}

		public void RemoveKey(int a, int b, int c)
		{
			var key = new Tuple<int, int, int>(a, b, c);
			if (data.ContainsKey(key))
				data.Remove(key);
		}

		public bool ContainsKey(int a, int b, int c)
		{
			var key = new Tuple<int, int, int>(a, b, c);
			return data.ContainsKey(key);
		}
	}
}
