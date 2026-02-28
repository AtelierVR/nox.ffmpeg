using System;
using System.Collections.Generic;
using Nox.FFmpeg.Workers;
using UnityEngine;
using UnityEngine.Events;

namespace Nox.FFmpeg.Connectors {
	/// <summary>
	/// Connecteur de visualisation audio avec rendu LineRenderer.
	/// - spectrumLine  : courbe FFT (N bandes log)
	/// - rmsLeftLine   : niveau RMS canal gauche (barre verticale)
	/// - rmsRightLine  : niveau RMS canal droit  (barre verticale)
	/// - peakLeftLine  : pic canal gauche
	/// - peakRightLine : pic canal droit
	/// </summary>
	public class AudioVisualizerConnector : BaseConnector {
		[Header("Référence")]
		public AudioWorker worker;

		[Header("Paramètres")]
		[Tooltip("Nombre de bandes du spectre FFT (puissance de 2 recommandée : 64, 128, 256)")]
		public int fftBands = 64;

		[Tooltip("Lissage temporel des niveaux (0 = aucun, proche de 1 = très lisse)")]
		[Range(0f, 0.99f)]
		public float smoothing = 0.85f;

		[Tooltip("Retombée du pic en secondes")]
		public float peakFalloff = 1.5f;

		[Header("LineRenderers")]
		[Tooltip("Courbe du spectre FFT")]
		public LineRenderer spectrumLine;

		[Tooltip("Barre RMS canal gauche")]
		public LineRenderer rmsLeftLine;

		[Tooltip("Barre RMS canal droit")]
		public LineRenderer rmsRightLine;

		[Tooltip("Marqueur de pic canal gauche")]
		public LineRenderer peakLeftLine;

		[Tooltip("Marqueur de pic canal droit")]
		public LineRenderer peakRightLine;

		[Header("Mise en page")]
		[Tooltip("Largeur totale de la courbe spectre (unités world)")]
		public float spectrumWidth = 10f;

		[Tooltip("Hauteur maximale de la courbe spectre (unités world)")]
		public float spectrumHeight = 3f;

		[Tooltip("Origine locale du spectre")]
		public Vector3 spectrumOrigin = Vector3.zero;

		[Tooltip("Largeur des barres RMS/Peak (unités world)")]
		public float barWidth = 0.4f;

		[Tooltip("Hauteur maximale des barres RMS/Peak (unités world)")]
		public float barHeight = 3f;

		[Tooltip("Position de la barre canal gauche")]
		public Vector3 rmsLeftOrigin = new Vector3(-1f, 0f, 0f);

		[Tooltip("Position de la barre canal droit")]
		public Vector3 rmsRightOrigin = new Vector3(-0.5f, 0f, 0f);

		// ─── Données exposées ─────────────────────────────────────────────────

		/// <summary>Niveau RMS lissé par canal (index 0 = L, 1 = R). Plage [0, 1].</summary>
		public float[] RmsLevels  { get; private set; } = Array.Empty<float>();

		/// <summary>Valeur de pic maintenue par canal. Plage [0, 1].</summary>
		public float[] PeakLevels { get; private set; } = Array.Empty<float>();

		/// <summary>Spectre FFT : amplitude par bande. Taille = fftBands.</summary>
		public float[] Spectrum   { get; private set; } = Array.Empty<float>();

		/// <summary>Niveau RMS mono global lissé. Plage [0, 1].</summary>
		public float RmsMono { get; private set; }

		/// <summary>Déclenché à chaque mise à jour des niveaux (main thread).</summary>
		public readonly UnityEvent OnLevelsUpdated = new();

		// ─── État interne ─────────────────────────────────────────────────────

		private int _channels;
		private int _frequency;

		private readonly object      _pcmLock    = new object();
		private readonly List<float> _pcmPending = new List<float>(8192);
		private bool                 _hasPending;

		private float[] _rmsRaw;
		private float[] _peakTimers;
		private float[] _fftBuffer;
		private float[] _spectrumRaw;

		// ─── Cycle de vie ─────────────────────────────────────────────────────

		private void Start() {
			worker ??= GetComponentInParent<AudioWorker>(true);
			if (!worker) {
				Debug.LogWarning("[AudioVisualizerConnector] Aucun AudioWorker trouvé.");
				return;
			}
			worker.AddQueue += OnAddQueue;
			InitLineRenderers();
		}

		private void OnDestroy() {
			if (worker != null)
				worker.AddQueue -= OnAddQueue;
		}

		private void Update() {
			if (!_hasPending)
				return;

			float[] snap;
			int     channels;

			lock (_pcmLock) {
				if (_pcmPending.Count == 0) { _hasPending = false; return; }
				snap      = _pcmPending.ToArray();
				channels  = _channels;
				_pcmPending.Clear();
				_hasPending = false;
			}

			EnsureBuffers(channels);
			ComputeRms(snap, channels);
			ComputeSpectrum(snap, channels);
			UpdatePeaks();
			RenderLines();

			OnLevelsUpdated.Invoke();
		}

		// ─── Réception PCM (thread audio) ─────────────────────────────────────

		private void OnAddQueue(ICollection<float> pcm, int channels, int frequency) {
			lock (_pcmLock) {
				_channels  = channels;
				_frequency = frequency;
				_pcmPending.Clear();
				foreach (var s in pcm)
					_pcmPending.Add(s);
				_hasPending = true;
			}
		}

		// ─── Initialisation des LineRenderers ─────────────────────────────────

		private void InitLineRenderers() {
			if (spectrumLine != null) {
				spectrumLine.positionCount = fftBands;
				spectrumLine.useWorldSpace = false;
			}
			InitBar(rmsLeftLine);
			InitBar(rmsRightLine);
			InitBar(peakLeftLine);
			InitBar(peakRightLine);
		}

		private static void InitBar(LineRenderer lr) {
			if (lr == null) return;
			lr.positionCount = 2;
			lr.useWorldSpace = false;
		}

		// ─── Rendu dans les LineRenderers ─────────────────────────────────────

		private void RenderLines() {
			RenderSpectrum();
			RenderBar(rmsLeftLine,   rmsLeftOrigin,  RmsLevels.Length  > 0 ? RmsLevels[0]  : 0f);
			RenderBar(rmsRightLine,  rmsRightOrigin, RmsLevels.Length  > 1 ? RmsLevels[1]  : 0f);
			RenderBar(peakLeftLine,  rmsLeftOrigin,  PeakLevels.Length > 0 ? PeakLevels[0] : 0f);
			RenderBar(peakRightLine, rmsRightOrigin, PeakLevels.Length > 1 ? PeakLevels[1] : 0f);
		}

		private void RenderSpectrum() {
			if (spectrumLine == null || Spectrum.Length == 0) return;

			if (spectrumLine.positionCount != fftBands)
				spectrumLine.positionCount = fftBands;

			for (int b = 0; b < fftBands; b++) {
				float x = spectrumOrigin.x + (float)b / (fftBands - 1) * spectrumWidth;
				float y = spectrumOrigin.y + Spectrum[b] * spectrumHeight;
				spectrumLine.SetPosition(b, new Vector3(x, y, spectrumOrigin.z));
			}
		}

		private void RenderBar(LineRenderer lr, Vector3 origin, float level) {
			if (lr == null) return;
			lr.SetPosition(0, origin);
			lr.SetPosition(1, origin + new Vector3(barWidth, level * barHeight, 0f));
		}

		// ─── Calculs ─────────────────────────────────────────────────────────

		private void EnsureBuffers(int channels) {
			if (RmsLevels.Length != channels) {
				RmsLevels   = new float[channels];
				PeakLevels  = new float[channels];
				_rmsRaw     = new float[channels];
				_peakTimers = new float[channels];
			}
			if (Spectrum.Length != fftBands) {
				Spectrum     = new float[fftBands];
				_spectrumRaw = new float[fftBands];
				if (spectrumLine != null)
					spectrumLine.positionCount = fftBands;
			}
		}

		private void ComputeRms(float[] pcm, int channels) {
			Span<double> sumSq = stackalloc double[channels];
			int          count = pcm.Length / channels;
			for (int i = 0; i < pcm.Length; i++)
				sumSq[i % channels] += pcm[i] * (double)pcm[i];

			float monoSum = 0f;
			for (int c = 0; c < channels; c++) {
				float rms    = count > 0 ? Mathf.Sqrt((float)(sumSq[c] / count)) : 0f;
				_rmsRaw[c]   = smoothing * _rmsRaw[c] + (1f - smoothing) * rms;
				RmsLevels[c] = Mathf.Clamp01(_rmsRaw[c]);
				monoSum      += RmsLevels[c];
			}
			RmsMono = channels > 0 ? monoSum / channels : 0f;
		}

		private void UpdatePeaks() {
			float dt = Time.deltaTime;
			for (int c = 0; c < RmsLevels.Length; c++) {
				if (RmsLevels[c] >= PeakLevels[c]) {
					PeakLevels[c]  = RmsLevels[c];
					_peakTimers[c] = peakFalloff;
				} else {
					_peakTimers[c] -= dt;
					if (_peakTimers[c] <= 0f)
						PeakLevels[c] = Mathf.Max(PeakLevels[c] - dt / peakFalloff, RmsLevels[c]);
				}
			}
		}

		private void ComputeSpectrum(float[] pcm, int channels) {
			if (fftBands <= 0) return;
			int monoLen = pcm.Length / channels;
			if (monoLen < 2) return;

			int fftSize = NextPow2(monoLen);
			if (_fftBuffer == null || _fftBuffer.Length != fftSize * 2)
				_fftBuffer = new float[fftSize * 2];

			for (int i = 0; i < fftSize; i++) {
				float mono = 0f;
				if (i < monoLen) {
					for (int c = 0; c < channels; c++)
						mono += pcm[i * channels + c];
					mono /= channels;
				}
				float hann            = 0.5f * (1f - Mathf.Cos(2f * Mathf.PI * i / (fftSize - 1)));
				_fftBuffer[i * 2]     = mono * hann;
				_fftBuffer[i * 2 + 1] = 0f;
			}

			FFT(_fftBuffer, fftSize);

			int   nyquist = fftSize / 2;
			float logMax  = Mathf.Log10(nyquist);

			for (int b = 0; b < fftBands; b++) {
				float t     = (float)b / fftBands;
				int   binLo = Mathf.Clamp(Mathf.RoundToInt(Mathf.Pow(10f, Mathf.Lerp(0f, logMax, t))),           0,        nyquist - 1);
				int   binHi = Mathf.Clamp(Mathf.RoundToInt(Mathf.Pow(10f, Mathf.Lerp(0f, logMax, (float)(b + 1) / fftBands))), binLo + 1, nyquist);

				float peak = 0f;
				for (int bin = binLo; bin < binHi; bin++) {
					float re  = _fftBuffer[bin * 2];
					float im  = _fftBuffer[bin * 2 + 1];
					float mag = Mathf.Sqrt(re * re + im * im) / fftSize;
					if (mag > peak) peak = mag;
				}
				_spectrumRaw[b] = smoothing * _spectrumRaw[b] + (1f - smoothing) * peak;
				Spectrum[b]     = _spectrumRaw[b];
			}
		}

		// ─── FFT Cooley-Tukey itérative ───────────────────────────────────────

		private static void FFT(float[] data, int n) {
			for (int i = 1, j = 0; i < n; i++) {
				int bit = n >> 1;
				for (; (j & bit) != 0; bit >>= 1) j ^= bit;
				j ^= bit;
				if (i < j) {
					(data[i * 2],     data[j * 2])     = (data[j * 2],     data[i * 2]);
					(data[i * 2 + 1], data[j * 2 + 1]) = (data[j * 2 + 1], data[i * 2 + 1]);
				}
			}
			for (int len = 2; len <= n; len <<= 1) {
				float ang = -2f * Mathf.PI / len;
				float wRe = Mathf.Cos(ang), wIm = Mathf.Sin(ang);
				for (int i = 0; i < n; i += len) {
					float curRe = 1f, curIm = 0f;
					for (int j = 0; j < len / 2; j++) {
						int   u   = (i + j) * 2, v = (i + j + len / 2) * 2;
						float uRe = data[u], uIm = data[u + 1];
						float tRe = curRe * data[v] - curIm * data[v + 1];
						float tIm = curRe * data[v + 1] + curIm * data[v];
						data[u]     = uRe + tRe;  data[u + 1] = uIm + tIm;
						data[v]     = uRe - tRe;  data[v + 1] = uIm - tIm;
						float nr = curRe * wRe - curIm * wIm;
						curIm = curRe * wIm + curIm * wRe;
						curRe = nr;
					}
				}
			}
		}

		private static int NextPow2(int n) {
			int p = 1;
			while (p < n) p <<= 1;
			return p;
		}
	}
}

