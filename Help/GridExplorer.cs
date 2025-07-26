using System;
using System.Collections.Generic;
using System.Diagnostics;
using log4net;
using Loki.Bot.Pathfinding;
using Loki.Game;
using Loki.Game.GameData;
using Loki.Common;
using System.Linq;

namespace Loki.Bot
{
	/// <summary>
	/// The default explorer for Exilebuddy.
	/// </summary>
	public class GridExplorer : IExplorer
	{
		/// <summary>The logger for this class.</summary>
		private static readonly ILog Log = Logger.GetLoggerInstanceForType();

		/// <summary>
		/// Node flags.
		/// </summary>
		public enum Flags
		{
			/// <summary></summary>
			Unknown = 0,

			/// <summary></summary>
			Known = 1,

			/// <summary></summary>
			Seen = 2,

			/// <summary></summary>
			Ignored = 4,

			/// <summary></summary>
			Disconnected = 8,
		}

		/// <summary>
		/// A node to explore.
		/// </summary>
		public class Node
		{
			/// <summary>
			/// Ctor.
			/// </summary>
			/// <param name="x"></param>
			/// <param name="y"></param>
			public Node(int x, int y)
			{
				Flags = Flags.Unknown;
				Location = new Vector2i(x*NodeSize, y*NodeSize);
				Index = new Vector2i(x, y);
				Size = new Vector2i(NodeSize, NodeSize);
				Center = Location + (Size/2);
				NavigableLocation = Center;
			}

			/// <summary>
			/// The current flags for this node.
			/// </summary>
			public Flags Flags { get; internal set; }

			/// <summary>
			/// Returns the top-left location of this box on the nav grid.
			/// </summary>
			public Vector2i Location { get; internal set; }

			/// <summary>
			/// Returns the size of this node.
			/// </summary>
			public Vector2i Size { get; internal set; }

			/// <summary>
			/// Gets this location in node-grid space.
			/// </summary>
			public Vector2i Index { get; internal set; }

			/// <summary>
			/// Returns the currently navigable point within this node.
			/// </summary>
			public Vector2i NavigableLocation { get; internal set; }

			/// <summary>
			/// Returns the center position of this node.
			/// </summary>
			public Vector2i Center { get; internal set; }
		}

		/// <summary>
		/// Ctor.
		/// </summary>
		public GridExplorer()
		{
			AutoResetOnAreaChange = true;
			TileKnownRadius = 7;
			TileSeenRadius = 5;
		}

		#region Implementation of ITickEvents / IStartStopEvents

		/// <summary> The object start callback. Do any initialization here. </summary>
		public void Start()
		{
		}

		/// <summary> The object tick callback. Do any update logic here. </summary>
		public void Tick()
		{
			if (!LokiPoe.IsInGame)
			{
				Unload();
				return;
			}

			Update();

			var myPosition = LokiPoe.MyPosition;

			var indexX = myPosition.X/NodeSize;
			var indexY = myPosition.Y/NodeSize;

			// If we're still in the same grid cell, and we have a location to move to, we don't need to update anything else.
			if (_lastIndexX == indexX && _lastIndexY == indexY && Location != Vector2i.Zero)
			{
				return;
			}

			// If we have moved, check tiles around us. Otherwise, we don't have to recheck tiles around us, since Location is Vector2i.Zero,
			// and we just need to find a new node from our pools. Basically, there's no case where we need to re-scan the tiles around us upon
			// not moving. This case comes about when there's an excess of unwalkable Known tiles, most likely from Incursion.
			if (_lastIndexX != indexX || _lastIndexY != indexY)
			{
				_lastIndexX = indexX;
				_lastIndexY = indexY;

				var cache = LokiPoe.TerrainData.Cache;
				LokiPoe.ReleaseFrameProfiler("GridExplorer.Tick", () =>
				{
					for (var i = -TileKnownRadius; i <= TileKnownRadius; i++)
					{
						for (var j = -TileKnownRadius; j <= TileKnownRadius; j++)
						{
							if (indexX + i >= 0 && indexX + i < Cols &&
								indexY + j >= 0 && indexY + j < Rows)
							{
								var node = _nodes[indexX + i, indexY + j];

								if (i <= TileSeenRadius && i >= -TileSeenRadius && j >= -TileSeenRadius &&
									j <= TileSeenRadius)
								{
									if (node.Flags == Flags.Unknown || node.Flags == Flags.Known)
									{
										GetNodesByFlag(node.Flags).Remove(node);

										//Log.DebugFormat("[Tick] [{0}, {1}] => {2}", indexX + i, indexY + j, Flags.Seen);
										node.Flags = Flags.Seen;

										UpdateNavigableLocation(cache, myPosition, node, false);

										GetNodesByFlag(node.Flags).Add(node);
									}
								}
								else
								{
									if (node.Flags == Flags.Unknown)
									{
										GetNodesByFlag(node.Flags).Remove(node);

										//Log.DebugFormat("[Tick] [{0}, {1}] => {2}", indexX + i, indexY + j, Flags.Known);
										node.Flags = Flags.Known;

										UpdateNavigableLocation(cache, myPosition, node, true);

										GetNodesByFlag(node.Flags).Add(node);
									}
								}
							}
						}
					}
				});
			}

			float unknown = GetNodesByFlag(Flags.Unknown).Count;
			float known = GetNodesByFlag(Flags.Known).Count;
			float seen = GetNodesByFlag(Flags.Seen).Count;

			PercentComplete = 100.0f*(seen)/(known + seen + unknown);

			// If we have a location, make sure it's to a Known node.
			if (Location != Vector2i.Zero)
			{
				var node = LocationNode;
				if (node == null || node.Flags != Flags.Known)
				{
					Location = Vector2i.Zero;
				}
			}

			// Find a new Known node to travel to.
			if (Location == Vector2i.Zero)
			{
				var nodes = GetNodesByFlag(Flags.Known);
				if (nodes.Count != 0)
				{
					var myPos = LokiPoe.MyPosition;
					Location =
						nodes.OrderBy(n => n.NavigableLocation.Distance(myPos))
							.ThenBy(TravelScore)
							.First()
							.NavigableLocation;
				}
			}
		}

		/// <summary> The object stop callback. Do any pre-dispose cleanup here. </summary>
		public void Stop()
		{
		}

		#endregion

		#region Implementation of IExplorer

		/// <summary>
		/// Should this explorer auto-reset on an area change. This is useful for when only one explorer is being used.
		/// </summary>
		public bool AutoResetOnAreaChange
		{
			get
			{
				return _autoResetOnAreaChange;
			}
			set
			{
				_autoResetOnAreaChange = value;
				Log.InfoFormat("[GridExplorer] AutoResetOnAreaChange is being set to {0}.", _autoResetOnAreaChange);
			}
		}

		/// <summary>
		/// How many tiles from the tile the player is currently on should we mark as "Known".
		/// "Known" tiles are simply tiles we should visit, as it helps reduce workload on larger
		/// areas, or areas with multiple islands.
		/// Default value: 7. Minimal value: 1.
		/// Do not modify this unless you know what you are doing.
		/// </summary>
		public int TileKnownRadius
		{
			get
			{
				return _tileKnownRadius;
			}
			set
			{
				_tileKnownRadius = value;
				if (_tileKnownRadius < 1)
					_tileKnownRadius = 1;
				Log.InfoFormat("[GridExplorer] TileKnownRadius is being set to {0}.", _tileKnownRadius);
			}
		}

		/// <summary>
		/// How many tiles from the tile the player is currently on should we mark as "Seen".
		/// "Seen" tiles are simply tiles we have explored, and loaded the contents of.
		/// Default value: 5. Minimal value: 1.
		/// Do not modify this unless you know what you are doing.
		/// </summary>
		public int TileSeenRadius
		{
			get
			{
				return _tileSeenRadius;
			}
			set
			{
				_tileSeenRadius = value;
				if (_tileSeenRadius < 1)
					_tileSeenRadius = 1;
				Log.InfoFormat("[GridExplorer] TileSeenRadius is being set to {0}.", _tileSeenRadius);
			}
		}

		private int _tileSeenRadius;
		private int _tileKnownRadius;
		private bool _autoResetOnAreaChange;

		/// <summary>
		/// Returns the current area for the explorer.
		/// </summary>
		public DatWorldAreaWrapper Area { get; private set; }

		/// <summary>
		/// Returns the hash for the current area.
		/// </summary>
		public uint Hash { get; private set; }

		/// <summary>
		/// Returns the current col count for the current area.
		/// </summary>
		public int Cols { get; private set; }

		/// <summary>
		/// Returns the current row count for the current area.
		/// </summary>
		public int Rows { get; private set; }

		/// <summary>This property returns if the explorer has a new location to explore.</summary>
		public bool HasLocation => Location != Vector2i.Zero;

		private float TravelScore(Node node)
		{
			float total = 0;
			float seen = 0;

			for (var i = -1; i <= 1; i++)
			{
				for (var j = -1; j <= 1; j++)
				{
					var nx = node.Index.X + i;
					var ny = node.Index.Y + j;

					if (nx >= 0 && nx < Cols && ny >= 0 && ny < Rows)
					{
						var n = _nodes[nx, ny];
						var f = n.Flags;
						if (f != Flags.Unknown)
						{
							total++;
						}
						if (f == Flags.Seen)
						{
							seen++;
						}
					}
				}
			}

			return 1.0f - (seen/total);
		}

		/// <summary>This property returns the current location that needs to be explored.</summary>
		public Vector2i Location { get; private set; } = Vector2i.Zero;

		/// <summary>
		/// Returns the Node associated with the current Location or null if none exists.
		/// </summary>
		public Node LocationNode
		{
			get
			{
				if (Location == Vector2i.Zero)
				{
					return null;
				}
				return GetNodeForLocation(Location);
			}
		}

		/// <summary>
		/// Returns the Node of a location.
		/// </summary>
		/// <param name="location">The location to get the node of.</param>
		/// <returns>A Node for the location or null if none exists.</returns>
		public Node GetNodeForLocation(Vector2i location)
		{
			try
			{
				return _nodes[location.X / NodeSize, location.Y / NodeSize];
			}
			catch (Exception ex)
			{
				Log.ErrorFormat("[GetNodeForLocation] Failed to get node for location {0}: {1}", location, ex);
			}
			return null;
		}

		/// <summary>This function tells the explorer a location should have flags changed.</summary>
		public void ForceNodeFlags(Vector2i location, Flags flags)
		{
			try
			{
				var node = GetNodeForLocation(location);
				if (node == null)
					return;

				GetNodesByFlag(node.Flags).Remove(node);

				node.Flags = flags;

				GetNodesByFlag(node.Flags).Add(node);

				var locationNode = LocationNode;
				if (locationNode != null && node.Index == locationNode.Index)
				{
					Location = Vector2i.Zero;
					Log.DebugFormat("[ForceNodeFlags] Now clearing the current location since its flags are being changed.");
				}
			}
			catch (Exception ex)
			{
				Log.ErrorFormat("[ForceNodeFlags] Failed to set flags {0} for location {1}: {2}", flags, location, ex);
			}
		}

		/// <summary>This function tells the explorer a location that should be ignored.</summary>
		public void Ignore(Vector2i location)
		{
			ForceNodeFlags(location, Flags.Ignored);
		}

		#endregion

		private int _lastIndexX = -1;
		private int _lastIndexY = -1;

		private Node[,] _nodes;

		private readonly Dictionary<Flags, List<Node>> _nodesByFlags = new Dictionary<Flags, List<Node>>();

		/// <summary>
		/// Returns a list of nodes that match the specified flag.
		/// </summary>
		/// <param name="flag"></param>
		/// <returns></returns>
		public List<Node> GetNodesByFlag(Flags flag)
		{
			return _nodesByFlags[flag];
		}

		internal const int NodeSize = 23;

		private bool _needsToUpdate = true;

		/// <summary>
		/// Returns the % complete of the explorer.
		/// </summary>
		public float PercentComplete { get; private set; }

		/// <summary>This function resets the current exploration status, so it's as if we were in a new area.</summary>
		public void Reset()
		{
			_needsToUpdate = true;
			Update();
		}

		private void Update()
		{
			if (!LokiPoe.IsInGame)
			{
				Unload();
				return;
			}

			if (!_needsToUpdate)
			{
				if (LokiPoe.LocalData.AreaHash != Hash)
				{
					if (AutoResetOnAreaChange)
					{
						_needsToUpdate = true;
					}
					else
					{
						return;
					}
				}
				else
				{
					return;
				}
			}

			Area = LokiPoe.CurrentWorldArea;
			Hash = LokiPoe.LocalData.AreaHash;

			Cols = LokiPoe.TerrainData.Cols;
			Rows = LokiPoe.TerrainData.Rows;

			PercentComplete = 0;

			Location = Vector2i.Zero;

			_lastIndexX = -1;
			_lastIndexY = -1;

			_nodesByFlags.Clear();
			_nodesByFlags.Add(Flags.Unknown, new List<Node>());
			_nodesByFlags.Add(Flags.Known, new List<Node>());
			_nodesByFlags.Add(Flags.Seen, new List<Node>());
			_nodesByFlags.Add(Flags.Ignored, new List<Node>());
			_nodesByFlags.Add(Flags.Disconnected, new List<Node>());

			_nodes = new Node[Cols, Rows];

			var myPosition = LokiPoe.MyPosition;

			var cache = LokiPoe.TerrainData.Cache;

			LokiPoe.ReleaseFrameProfiler("GridExplorer.Update", () =>
			{
				var sw = Stopwatch.StartNew();

				Log.DebugFormat("[GridExplorer] Now segmenting the current area.");
				for (var x = 0; x < Cols; x++)
				{
					for (var y = 0; y < Rows; y++)
					{
						var node = new Node(x, y);
						_nodes[x, y] = node;
						UpdateNavigableLocation(cache, myPosition, node, false);
						GetNodesByFlag(node.Flags).Add(node);
					}
				}
				Log.DebugFormat("[GridExplorer] Area segmentation complete {0}.", sw.Elapsed);
			});

			_needsToUpdate = false;
		}

		private void UpdateNavigableLocation(CachedTerrainData cache, Vector2i position, Node node,
			bool pathfindCheck)
		{
			UpdateNavigableLocation(cache, position, node, node.NavigableLocation, pathfindCheck);
		}

		private void UpdateNavigableLocation(CachedTerrainData cache, Vector2i position, Node node, Vector2i location,
			bool pathfindCheck)
		{
			if (location == default(Vector2i))
			{
				location = node.Center;
			}

			var dc = false;

			if (WalkabilityGrid.IsWalkable(cache.Data, cache.BPR, location, cache.Value))
			{
				node.NavigableLocation = location;
			}
			else
			{
				var pts = new List<Vector2i>();
				for (var x = node.Location.X; x < node.Location.X + NodeSize; x++)
				{
					for (var y = node.Location.Y; y < node.Location.Y + NodeSize; y++)
					{
						pts.Add(new Vector2i(x, y));
					}
				}

				var pt =
					pts.OrderBy(v => v.DistanceSqr(location))
						.FirstOrDefault(e => WalkabilityGrid.IsWalkable(cache.Data, cache.BPR, e, cache.Value));
				if (pt != default(Vector2i))
				{
					node.NavigableLocation = pt;
				}
				else
				{
					dc = true;
				}
			}

			if (pathfindCheck && !dc)
			{
				var cmd = new PathfindingCommand(position, node.NavigableLocation, 15, false, 9);
				if (!ExilePather.FindPath(ref cmd, true))
				{
					dc = true;
				}
			}

			if (dc)
			{
				node.Flags = Flags.Disconnected;
			}
		}

		/// <summary>
		/// This function unloads all exploration data. This must be implemented to avoid excessive memory usage.
		/// </summary>
		public void Unload()
		{
			_needsToUpdate = true;

			PercentComplete = 0;

			Location = Vector2i.Zero;

			_lastIndexX = -1;
			_lastIndexY = -1;

			_nodesByFlags.Clear();

			_nodes = null;

			Area = null;
			Hash = 0;
			Cols = 0;
			Rows = 0;
		}
	}
}