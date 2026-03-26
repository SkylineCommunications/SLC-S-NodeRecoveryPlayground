namespace NodeRecoveryLocalClusterState
{
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading;
	using System.Threading.Tasks;
	using Skyline.DataMiner.Analytics.GenericInterface;
	using Skyline.DataMiner.Net.Messages;
	using Skyline.DataMiner.Net.NodeRecovery.Requests;

	/// <summary>
	/// Represents a data source.
	/// See: https://aka.dataminer.services/gqi-external-data-source for a complete example.
	/// </summary>
	[GQIMetaData(Name = "NodeRecovery Playground - Local Cluster State")]
	public sealed class LocalClusterState : IGQIDataSource, IGQIOnInit, IGQIUpdateable
	{
		private static readonly GQIColumn[] _columns = new GQIColumn[]
		{
			new GQIIntColumn("Source Node Id"),
			new GQIIntColumn("Destination Node Id"),
			new GQIStringColumn("Node State"),
		};

		private readonly TimeSpan _updateInterval = TimeSpan.FromSeconds(5);
		private readonly HashSet<string> _rowKeys = new HashSet<string>();

		private GQIDMS _dms;
		private IGQILogger _logger;
		private bool _isUpdating = true;

		/// <inheritdoc />
		public OnInitOutputArgs OnInit(OnInitInputArgs args)
		{
			_dms = args.DMS;
			_logger = args.Logger;
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
						// TODO optimize to check both ints instead of strings

						var rows = GetRows();
						foreach (var row in rows)
						{
							_rowKeys.Add(row.Key);
							updater.AddRow(row); // behaves as add or update
						}

						var keysToRemove = _rowKeys.Except(rows.Select(row => row.Key)).ToArray();
						foreach (var key in keysToRemove)
						{
							_rowKeys.Remove(key);
							updater.RemoveRow(key);
						}
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
			var resps = _dms.SendMessages(new DMSMessage[]
			{
				new GetInfoMessage(InfoType.DataMinerInfo),
			});

			var agents = resps.OfType<GetDataMinerInfoResponseMessage>().Select(info => info.ID).ToArray();
			var responses = new (int, LocalClusterStateResponse)[agents.Length];

			Parallel.ForEach(agents, (agentId, _, idx) =>
			{
				try
				{
					var resp = _dms.SendMessage(new LocalClusterStateRequest() { TargetDataMinerId = agentId }) as LocalClusterStateResponse;
					responses[idx] = (agentId, resp);
				}
				catch
				{
					// Nothing to do, node might be unreachable
					return;
				}
			});

			var rows = new List<GQIRow>(agents.Length * agents.Length);
			foreach (var (srcNodeId, localClusterState) in responses)
			{
				if (localClusterState == null)
					continue;

				foreach (var kvp in localClusterState.ClusterState.OrderBy(kvp => kvp.Key))
				{
					var dstNodeId = kvp.Key;
					var nodeStateInfo = kvp.Value;
					var rowKey = $"{srcNodeId}->{dstNodeId}";

					var cells = new GQICell[]
					{
						new GQICell() { Value = srcNodeId, DisplayValue = srcNodeId.ToString() },
						new GQICell() { Value = dstNodeId, DisplayValue = dstNodeId.ToString() },
						new GQICell() { Value = nodeStateInfo.State.ToString(), DisplayValue = nodeStateInfo.State.ToString() },
					};

					rows.Add(new GQIRow(rowKey, cells));
				}
			}

			return rows.ToArray();
		}
	}
}
