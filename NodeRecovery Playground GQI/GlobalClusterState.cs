namespace NodeRecoveryGlobalClusterState
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;
	using Skyline.DataMiner.Analytics.GenericInterface;
	using Skyline.DataMiner.Net.Messages;
	using Skyline.DataMiner.Net.NodeRecovery.Requests;

	/// <summary>
	/// Represents a data source.
	/// See: https://aka.dataminer.services/gqi-external-data-source for a complete example.
	/// </summary>
	[GQIMetaData(Name = "NodeRecovery Playground - Global Cluster State")]
	public sealed class GlobalClusterState : IGQIDataSource, IGQIOnInit, IGQIUpdateable
	{
		private static readonly GQIColumn[] _columns = new GQIColumn[]
		{
			new GQIIntColumn("Node Id"),
			new GQIStringColumn("Node Name"),
			new GQIStringColumn("Node State"),
			new GQIDoubleColumn("Position X"),
			new GQIDoubleColumn("Position Y"),
			new GQIBooleanColumn("Is Leader"),
			new GQIBooleanColumn("In Maintenance"),
			new GQIBooleanColumn("Is Local Connected Node"),
		};

		private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(5);

		private GQIDMS _dms;
		private IGQILogger _logger;
		private bool _isUpdating = true;

		private int _localDmaId = -1;
		private Dictionary<int, string> _dmaNames = new Dictionary<int, string>();

		/// <inheritdoc />
		public OnInitOutputArgs OnInit(OnInitInputArgs args)
		{
			_dms = args.DMS;
			_logger = args.Logger;

			// store all dma names by id for later lookups
			var resps = _dms.SendMessages(new DMSMessage[]
			{
				new GetInfoMessage(InfoType.DataMinerInfo),
			});

			var infos = resps?.OfType<GetDataMinerInfoResponseMessage>().ToArray();

			if (infos is null || infos.Length == 0)
			{
				_logger.Error("No DataMinerInfo responses received, unable to determine local DMA ID and names of DMAs.");
				return default;
			}

			// the local dma one should be the first
			_localDmaId = infos.First().ID;
			_dmaNames = infos.ToDictionary(info => info.ID, info => info.Name);

			return default;
		}

		/// <inheritdoc />
		public GQIColumn[] GetColumns() => _columns;

		/// <inheritdoc />
		public GQIPage GetNextPage(GetNextPageInputArgs args)
		{
			return new GQIPage(GetRows());
		}

		/// <inheritdoc />
		public void OnStartUpdates(IGQIUpdater updater)
		{
			Task.Run(() =>
			{
				while (_isUpdating)
				{
					try
					{
						var rows = GetRows();
						foreach (var row in rows)
							updater.AddRow(row); // behaves as add or update
					}
					catch (Exception ex)
					{
						// Nothing to do, node might be unreachable
						_logger.Error($"Error while updating rows: {ex}");
					}

					Thread.Sleep(_updateInterval);
				}
			});
		}

		/// <inheritdoc />
		public void OnStopUpdates()
		{
			_isUpdating = false;
		}

		private GQIRow[] GetRows()
		{
			try
			{
				var resps = _dms.SendMessages(new DMSMessage[]
				{
					new GlobalClusterStateRequest(),
				});

				var globalClusterState = resps.OfType<GlobalClusterStateResponse>().Single();
				var clusterSize = globalClusterState.ClusterState.Count;
				var leaderNodeId = globalClusterState.LeaderNodeId;

				var rows = new List<GQIRow>(clusterSize);
				foreach (var (idx, kvp) in globalClusterState.ClusterState.OrderBy(kvp => kvp.Key).Select((kvp, idx) => (idx, kvp)))
				{
					var nodeId = kvp.Key;
					var nodeStateInfo = kvp.Value;
					var isLeader = leaderNodeId == nodeId;
					var isLocalConnectedNode = _localDmaId == nodeId;
					var name = string.Empty;
					_dmaNames.TryGetValue(nodeId, out name);
					var (x, y) = GetPositionCircle(idx, clusterSize);

					var rowKey = nodeId.ToString();
					var cells = new GQICell[]
					{
						new GQICell() { Value = nodeId, DisplayValue = nodeId.ToString() },
						new GQICell() { Value = name, DisplayValue = name },
						new GQICell() { Value = nodeStateInfo.State.ToString(), DisplayValue = nodeStateInfo.State.ToString() },
						new GQICell() { Value = x, DisplayValue = x.ToString() },
						new GQICell() { Value = y, DisplayValue = y.ToString() },
						new GQICell() { Value = isLeader, DisplayValue = isLeader.ToString() },
						new GQICell() { Value = nodeStateInfo.InMaintenance, DisplayValue = nodeStateInfo.InMaintenance.ToString() },
						new GQICell() { Value = isLocalConnectedNode, DisplayValue = isLocalConnectedNode.ToString() },
					};

					rows.Add(new GQIRow(rowKey, cells));
				}

				return rows.ToArray();
			}
			catch
			{
				// failed, most likely there is no leader currently, just return all nodes without state.
				// So local states (edges) can still be shown
				var rows = _dmaNames.OrderBy(kvp => kvp.Key).Select((kvp, idx) =>
				{
					var nodeId = kvp.Key;
					var name = kvp.Value;
					var isLocalConnectedNode = _localDmaId == nodeId;
					var (x, y) = GetPositionCircle(idx, _dmaNames.Count);

					var rowKey = nodeId.ToString();
					var cells = new GQICell[]
					{
						new GQICell() { Value = nodeId, DisplayValue = nodeId.ToString() },
						new GQICell() { Value = name, DisplayValue = name },
						new GQICell() { Value = "None", DisplayValue = "None" },
						new GQICell() { Value = x, DisplayValue = x.ToString() },
						new GQICell() { Value = y, DisplayValue = y.ToString() },
						new GQICell() { Value = false, DisplayValue = "false" },
						new GQICell() { Value = false, DisplayValue = "false" },
						new GQICell() { Value = isLocalConnectedNode, DisplayValue = isLocalConnectedNode.ToString() },
					};

                    return new GQIRow(rowKey, cells);
				}).ToArray();

				return rows;
			}
		}

		private (double X, double Y) GetPositionCircle(double idx, double count)
		{
            // default viewport in node edge is 100x100, this makes the scaling of the node template play nice
            double viewport = 100d;

            double viewportHalf = viewport * 0.5d;

            if (count == 1)
				return (viewportHalf, viewportHalf); // if there is only one node, just put it in the middle

            // To get the coordinates of n nodes evenly spaced on a circle, you can use the following formulas, where r is the radius of the circle and (x0, y0) is the center of the circle:
            // x = x0 + r * cos(2PI k/n)
            // y = y0 + r * sin(2PI k/n)
            // where k ranges from 0 to n-1 (or 1 to n)

            // also offset the cos so the first node starts at the top (offset with PI / 2)
            // also do minus instead of plus so the nodes go clockwise

            // radius is half of viewport size, then take 80% to have some buffer space rest of node template + padding
            double radius = viewportHalf * 0.8d; 

			double radians = (Math.PI / 2) - (2 * Math.PI * idx / count);
			double x = viewportHalf + (radius * Math.Cos(radians));
			double y = viewportHalf + (radius * Math.Sin(radians));

			return (x, y);
		}
	}
}
