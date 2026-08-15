using BlazorDLR.Shared.Services;
using DLR.Core.Contracts.Tracks;
using DLR.Core.Tracks;

namespace BlazorDLR.Shared.State;

/// <summary>
/// The track this device is recording, and the two settings that govern it (§4.4, §15.1, §18.6).
/// <para>
/// <strong>Device-local until the rider says otherwise.</strong> Points are appended to a store
/// this app owns on this phone and go nowhere until <see cref="SaveAsync"/> is called. That is
/// what makes the private-area choice at save time honest rather than a loophole: while the track
/// is unsaved it lives in the same place the private area itself does, and the filter runs on the
/// one path that takes it off the device.
/// </para>
/// <para>
/// <strong>Fed before the publish gates, not after.</strong> <see cref="LocationBroadcastState"/>
/// offers every raw fix here and then decides separately whether to broadcast it. A recorder
/// downstream of the §4.2 gate would give a rider on Eco a track sampled every 50 m however fine
/// an interval they had chosen, and a recorder downstream of the §10.1 gate could never offer the
/// choice this screen offers.
/// </para>
/// <para>
/// <strong>Written through, not held.</strong> The list is in memory and flushed to the device
/// store every <see cref="FlushEveryPoints"/> points and on every deliberate stop. Writing on
/// every point would gzip the whole ride once a second; writing only at the end would lose the
/// ride to the one thing that actually happens — Android reclaiming the app mid-tour.
/// </para>
/// </summary>
public sealed class TrackRecordingState
{
	/// <summary>The <see cref="IDeviceSettings"/> key for the on/off switch.</summary>
	public const string EnabledStorageKey = "dlr.track-recording";

	/// <summary>The <see cref="IDeviceSettings"/> key for the chosen interval, in metres.</summary>
	public const string IntervalStorageKey = "dlr.track-recording-interval";

	/// <summary>The <see cref="IDeviceSettings"/> key for the track in progress.</summary>
	public const string TrackStorageKey = "dlr.track-recording-current";

	/// <summary>
	/// How many appended points force a flush. At the default 10 m interval a rider at 60 km/h
	/// produces one about every 0.6 s, so this is a write every ten seconds or so — cheap against
	/// how much of a ride it is willing to lose.
	/// </summary>
	private const int FlushEveryPoints = 16;

	private readonly IDeviceSettings _settings;
	private readonly IApiClient _api;
	private readonly PrivateAreaState _privateAreas;
	private readonly Lock _gate = new();

	private readonly List<TrackPoint> _points = [];
	private readonly List<int> _segmentStarts = [];

	private Guid _clientGuid = Guid.NewGuid();
	private bool _enabled = TrackRecording.DefaultEnabled;
	private double _intervalM = TrackRecording.DefaultIntervalM;
	private bool _loaded;
	private int _sinceFlush;

	/// <summary>
	/// Whether the next kept fix begins a new segment. Set when the receiver stops, so a ride
	/// resumed after lunch is two segments rather than one with a motorway drawn through a café.
	/// </summary>
	private bool _breakPending = true;

	private TrackStats? _stats;

	/// <summary>Creates the recorder over one host's seams.</summary>
	/// <param name="settings">Where the switch, the interval and the track in progress are kept.</param>
	/// <param name="api">Where a saved track is uploaded (§6.3).</param>
	/// <param name="privateAreas">The §10.1 area, consulted at save time rather than at capture time.</param>
	public TrackRecordingState(IDeviceSettings settings, IApiClient api, PrivateAreaState privateAreas)
	{
		_settings = settings;
		_api = api;
		_privateAreas = privateAreas;
	}

	/// <summary>Fired after <see cref="LoadAsync"/> first resolves, and after anything below it moves.</summary>
	public event Action? Changed;

	/// <summary>Whether this device keeps a track while the GPS is running.</summary>
	public bool IsEnabled => _enabled;

	/// <summary>How far the rider must move before another point is kept, in metres.</summary>
	public double IntervalM => _intervalM;

	/// <summary>Whether the device store has been read yet.</summary>
	public bool IsLoaded => _loaded;

	/// <summary>Whether there is a track in progress at all.</summary>
	public bool HasTrack => PointCount > 0;

	/// <summary>How many points the track in progress holds.</summary>
	public int PointCount
	{
		get
		{
			lock (_gate)
			{
				return _points.Count;
			}
		}
	}

	/// <summary>
	/// The numbers the Location screen prints about the track in progress, or <c>null</c> when
	/// there is none.
	/// <para>
	/// Computed on demand and cached until the next point, because <see cref="TrackStats.From"/>
	/// walks the whole track — recomputing it per appended point would turn a day's recording into
	/// quadratic work on the one device that has the least to spare.
	/// </para>
	/// </summary>
	public TrackStats? Stats
	{
		get
		{
			lock (_gate)
			{
				if (_points.Count == 0)
				{
					return null;
				}

				return _stats ??= TrackStats.From(new TrackGeometry([.. _points], _segmentStarts));
			}
		}
	}

	/// <summary>
	/// Whether the track has reached <see cref="TrackRecording.MaxPoints"/> and is no longer
	/// growing. What the screen says out loud rather than letting a rider believe it is still
	/// recording.
	/// </summary>
	public bool IsFull => PointCount >= TrackRecording.MaxPoints;

	/// <summary>
	/// Reads the switch, the interval and any track left over from the last run. Idempotent — the
	/// settings screen and the receiver both call it without coordinating.
	/// <para>
	/// Callers must run this <em>after</em> first render on the web: the browser store is behind JS
	/// interop, which does not exist during the prerender pass.
	/// </para>
	/// </summary>
	/// <param name="cancellationToken">Cancels the read.</param>
	public async Task LoadAsync(CancellationToken cancellationToken = default)
	{
		if (_loaded)
		{
			return;
		}

		// Set before the reads so two callers do not start two round trips, on
		// GpsProfileState's reasoning — nothing here fails closed, so an in-flight read costs at
		// worst a first fix landing on the shipped defaults.
		_loaded = true;

		_enabled = await _settings.GetAsync(EnabledStorageKey, cancellationToken) switch
		{
			"1" => true,
			"0" => false,
			_ => TrackRecording.DefaultEnabled,
		};

		_intervalM = TrackRecording.DecodeInterval(
			await _settings.GetAsync(IntervalStorageKey, cancellationToken));

		if (TrackRecording.Decode(await _settings.GetAsync(TrackStorageKey, cancellationToken)) is { } recovered)
		{
			lock (_gate)
			{
				_clientGuid = recovered.ClientGuid;
				_points.AddRange(recovered.Geometry.Points);
				_segmentStarts.AddRange(recovered.Geometry.SegmentStarts);
				_stats = null;
			}

			// Whatever happened between the last flush and this launch is a hole of unknown
			// length, so the ride does not resume in the segment it left off in.
			_breakPending = true;
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Turns recording on or off for this device. Turning it off keeps whatever has been recorded
	/// so far — the rider asked to stop adding to a track, not to lose one.
	/// </summary>
	/// <param name="enabled">The rider's choice.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
	{
		_loaded = true;

		if (_enabled == enabled)
		{
			return;
		}

		_enabled = enabled;

		// Turning it back on after a gap is a new segment for the same reason a relaunch is.
		_breakPending = true;

		Changed?.Invoke();
		await _settings.SetAsync(EnabledStorageKey, enabled ? "1" : "0", cancellationToken);
	}

	/// <summary>
	/// Chooses how far apart kept points are. Takes effect on the next fix; the part of the track
	/// already recorded is left at the interval it was recorded at, which is the only answer that
	/// does not rewrite a ride somebody already rode.
	/// </summary>
	/// <param name="intervalM">One of <see cref="TrackRecording.IntervalsM"/>.</param>
	/// <param name="cancellationToken">Cancels the write.</param>
	public async Task SetIntervalAsync(double intervalM, CancellationToken cancellationToken = default)
	{
		_loaded = true;

		if (!TrackRecording.IntervalsM.Contains(intervalM) || _intervalM == intervalM)
		{
			return;
		}

		_intervalM = intervalM;

		Changed?.Invoke();
		await _settings.SetAsync(IntervalStorageKey, TrackRecording.EncodeInterval(intervalM), cancellationToken);
	}

	/// <summary>
	/// Offers one fix to the recorder. Called from the receiver's pump for every fix the platform
	/// produces, <em>before</em> the §4.2 and §10.1 gates decide about publishing it.
	/// </summary>
	/// <param name="fix">The fix as the platform reported it.</param>
	/// <param name="cancellationToken">Cancels the flush this may trigger.</param>
	/// <returns>Whether the fix was kept.</returns>
	public async Task<bool> OfferAsync(LocationFix fix, CancellationToken cancellationToken = default)
	{
		if (!_enabled)
		{
			return false;
		}

		bool flush;

		lock (_gate)
		{
			if (_points.Count >= TrackRecording.MaxPoints)
			{
				return false;
			}

			TrackPoint? last = _points.Count > 0 ? _points[^1] : null;
			RecordDecision decision = TrackRecording.Decide(last, fix, _intervalM);

			if (decision == RecordDecision.Drop)
			{
				// Including when a break is pending: the break has to hang on a point worth
				// keeping, so it waits for the rider to move rather than pinning itself to a fix
				// taken where the last one already is.
				return false;
			}

			if (decision == RecordDecision.StartSegment || _breakPending)
			{
				_segmentStarts.Add(_points.Count);
				_breakPending = false;
			}

			_points.Add(TrackRecording.ToPoint(fix));
			_stats = null;

			flush = ++_sinceFlush >= FlushEveryPoints;
		}

		if (flush)
		{
			await FlushAsync(cancellationToken);
		}

		Changed?.Invoke();

		return true;
	}

	/// <summary>
	/// Writes the track in progress to the device store. What
	/// <see cref="LocationBroadcastState"/> calls when the receiver stops, so the last few points
	/// of a ride are not the ones lost to a phone the OS reclaims in a pocket.
	/// </summary>
	/// <param name="cancellationToken">Cancels the write.</param>
	public async Task FlushAsync(CancellationToken cancellationToken = default)
	{
		string? encoded;

		lock (_gate)
		{
			_sinceFlush = 0;
			encoded = _points.Count == 0 ? null : TrackRecording.Encode(_clientGuid, Snapshot());
		}

		if (encoded is null)
		{
			await _settings.RemoveAsync(TrackStorageKey, cancellationToken);

			return;
		}

		await _settings.SetAsync(TrackStorageKey, encoded, cancellationToken);
	}

	/// <summary>
	/// Uploads the track in progress as one of the rider's own tracks (§6.3) and clears it.
	/// <para>
	/// The upload is idempotent on the identifier minted when the track started, so a save that
	/// failed after the server stored it can be pressed again without producing two rides. The
	/// track is only cleared once the server has answered.
	/// </para>
	/// </summary>
	/// <param name="name">What to call it, or <c>null</c> to leave the server's default.</param>
	/// <param name="excludePrivateArea">
	/// Whether to cut out every point inside the §10.1 area first. On by default on the screen: the
	/// track is the one artefact of a ride that outlives it, and it starts and ends at home.
	/// </param>
	/// <param name="cancellationToken">Cancels the upload.</param>
	/// <returns>The stored track.</returns>
	/// <exception cref="InvalidOperationException">Nothing is left to save once the filter has run.</exception>
	public async Task<TrackSummary> SaveAsync(
		string? name,
		bool excludePrivateArea,
		CancellationToken cancellationToken = default)
	{
		// The area is read rather than assumed: PrivateAreaState.HidesLocation answers "hide"
		// until it has been read, which is right for a fix about to be broadcast and wrong here —
		// it would silently delete the whole ride.
		await _privateAreas.LoadAsync(cancellationToken);

		Guid clientGuid;
		TrackGeometry geometry;

		lock (_gate)
		{
			clientGuid = _clientGuid;
			geometry = Snapshot();
		}

		if (excludePrivateArea)
		{
			geometry = TrackRecording.WithoutPrivateArea(geometry, _privateAreas.Area);
		}

		if (geometry.Points.Count < TrackEditor.MinimumSurvivingPoints)
		{
			throw new InvalidOperationException(
				excludePrivateArea && _privateAreas.Area is not null
					? "Every point on this track is inside your private area, so there is nothing to save."
					: $"A track needs at least {TrackEditor.MinimumSurvivingPoints} points.");
		}

		TrackSummary saved = await _api.UploadTrackAsync(
			new UploadTrackRequest(
				clientGuid,
				geometry.Points,
				geometry.SegmentStarts,
				string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
				TrackSourceDto.Recorded),
			cancellationToken);

		await DiscardAsync(cancellationToken);

		return saved;
	}

	/// <summary>
	/// Throws the track in progress away and starts a fresh one. What the screen's delete button
	/// calls, and what <see cref="SaveAsync"/> calls once the server has the ride.
	/// </summary>
	/// <param name="cancellationToken">Cancels the removal.</param>
	public async Task DiscardAsync(CancellationToken cancellationToken = default)
	{
		lock (_gate)
		{
			_points.Clear();
			_segmentStarts.Clear();
			_stats = null;
			_sinceFlush = 0;

			// A new identifier, or the next upload would collide with the one just stored and the
			// server would idempotently answer with the old ride.
			_clientGuid = Guid.NewGuid();
		}

		_breakPending = true;

		Changed?.Invoke();

		await _settings.RemoveAsync(TrackStorageKey, cancellationToken);
	}

	/// <summary>A copy of what is held, safe to read outside the lock.</summary>
	private TrackGeometry Snapshot() => new([.. _points], _segmentStarts);
}
