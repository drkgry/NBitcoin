﻿#if HAS_SPAN
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace NBitcoin.Secp256k1.Musig
{
	class SessionValues
	{
		internal Scalar b;
		internal Scalar e;
		internal GE r;

		internal SessionValues Clone()
		{
			var c = new SessionValues()
			{
				b = b,
				e = e,
				r = r
			};
			return c;
		}
	}
#if SECP256K1_LIB
	public
#else
	internal
#endif
	class MusigContext
	{
		internal byte[] pk_hash = new byte[32];
		internal FE second_pk_x;
		internal bool pk_parity;
		internal bool is_tweaked;
		internal Scalar scalar_tweak;
		internal readonly byte[] msg32;
		internal Scalar gacc;
		internal bool processed_nonce;
		internal SessionValues? SessionCache;

		private MusigPubNonce? aggregateNonce;
		public MusigPubNonce? AggregateNonce => aggregateNonce;
		public ECPubKey AggregatePubKey => aggregatePubKey;

		private ECPubKey aggregatePubKey;
		private Context ctx;


		public MusigContext(MusigContext musigContext)
		{
			if (musigContext == null)
				throw new ArgumentNullException(nameof(musigContext));
			musigContext.pk_hash.CopyTo(pk_hash.AsSpan());
			second_pk_x = musigContext.second_pk_x;
			pk_parity = musigContext.pk_parity;
			is_tweaked = musigContext.is_tweaked;
			scalar_tweak = musigContext.scalar_tweak;
			gacc = musigContext.gacc;
			tacc = musigContext.tacc;
			processed_nonce = musigContext.processed_nonce;
			SessionCache = musigContext.SessionCache?.Clone();
			aggregateNonce = musigContext.aggregateNonce;
			aggregatePubKey = musigContext.aggregatePubKey;
			ctx = musigContext.ctx;
			msg32 = musigContext.msg32;
		}

		public MusigContext Clone()
		{
			return new MusigContext(this);
		}

		internal MusigContext(ECPubKey[] pubKeys, ReadOnlySpan<byte> msg32)
		{
			if (pubKeys == null)
				throw new ArgumentNullException(nameof(pubKeys));
			if (pubKeys.Length is 0)
				throw new ArgumentException(nameof(pubKeys), "There should be at least one pubkey in pubKeys");
			if (!(msg32.Length is 32))
				throw new ArgumentNullException(nameof(msg32), "msg32 should be 32 bytes.");
			this.aggregatePubKey = ECXOnlyPubKey.MusigAggregate(pubKeys, this);
			this.ctx = pubKeys[0].ctx;
			this.msg32 = msg32.ToArray();
		}

		public ECPubKey Tweak(ReadOnlySpan<byte> tweak32)
		{
			if (processed_nonce)
				throw new InvalidOperationException("This function can only be called before MusigContext.Process");
			if (is_tweaked)
				throw new InvalidOperationException("This function can only be called once");
			if (tweak32.Length != 32)
				throw new ArgumentException(nameof(tweak32), "The tweak should have a size of 32 bytes");
			scalar_tweak = new Scalar(tweak32, out int overflow);
			if (overflow == 1)
				throw new ArgumentException(nameof(tweak32), "The tweak is overflowing");
			var output = aggregatePubKey.ToXOnlyPubKey().AddTweak(tweak32);
			pk_parity = output.Q.y.IsOdd;
			if (pk_parity)
			{
				gacc = gacc.Negate();
				tacc = tacc.Negate();
			}
			tacc += scalar_tweak;
			is_tweaked = true;
			aggregatePubKey = output;
			return output;
		}
		ECPubKey? adaptor;
		private Scalar tacc = Scalar.Zero;

		public void UseAdaptor(ECPubKey adaptor)
		{
			if (processed_nonce)
				throw new InvalidOperationException("This function can only be called before MusigContext.Process");
			this.adaptor = adaptor;
		}

		public void ProcessNonces(MusigPubNonce[] nonces)
		{
			Process(MusigPubNonce.Aggregate(nonces));
		}
		public void Process(MusigPubNonce aggregatedNonce)
		{
			if (processed_nonce)
				throw new InvalidOperationException($"Nonce already processed");
			var q = this.AggregatePubKey;

			SessionValues session_cache = new SessionValues();
			Span<byte> qbytes = stackalloc byte[32];
			Span<GEJ> aggnonce_ptj = stackalloc GEJ[2];
			aggnonce_ptj[0] = aggregatedNonce.K1.ToGroupElementJacobian();
			aggnonce_ptj[1] = aggregatedNonce.K2.ToGroupElementJacobian();

			q.Q.x.WriteToSpan(qbytes);
			/* Add public adaptor to nonce */
			if (adaptor != null)
			{
				aggnonce_ptj[0] = aggnonce_ptj[0].AddVariable(adaptor.Q);
			}

			secp256k1_musig_nonce_process_internal(this.ctx.EcMultContext, out var r, out session_cache.b, aggnonce_ptj, qbytes, msg32);
			Span<byte> rbytes = stackalloc byte[32];
			ECXOnlyPubKey.secp256k1_xonly_ge_serialize(rbytes, ref r);
			/* Compute messagehash and store in session cache */
			Span<byte> buff = stackalloc byte[32];
			using SHA256 sha = new SHA256();
			sha.InitializeTagged(ECXOnlyPubKey.TAG_BIP0340Challenge);
			sha.Write(rbytes);
			sha.Write(qbytes);
			sha.Write(msg32);
			sha.GetHash(buff);
			session_cache.e = new Scalar(buff);
			session_cache.r = r;

			SessionCache = session_cache;
			processed_nonce = true;
			this.aggregateNonce = aggregatedNonce;
		}

		internal static void secp256k1_musig_nonce_process_internal(
			ECMultContext ecmult_ctx,
			out GE r,
			out Scalar b,
			Span<GEJ> aggnoncej,
			ReadOnlySpan<byte> qbytes,
			ReadOnlySpan<byte> msg)
		{
			Span<byte> noncehash = stackalloc byte[32];
			Span<GE> aggnonce = stackalloc GE[2];
			aggnonce[0] = aggnoncej[0].ToGroupElement();
			aggnonce[1] = aggnoncej[1].ToGroupElement();
			secp256k1_musig_compute_noncehash(noncehash, aggnonce, qbytes, msg);

			/* aggnonce = aggnonces[0] + b*aggnonces[1] */
			b = new Scalar(noncehash);
			var fin_nonce_ptj = ecmult_ctx.Mult(aggnoncej[1], b, null);
			fin_nonce_ptj = fin_nonce_ptj.Add(aggnonce[0]);
			r = fin_nonce_ptj.IsInfinity ? EC.G : fin_nonce_ptj.ToGroupElement();
			r = r.NormalizeYVariable();
		}

		/* hash(summed_nonces[0], summed_nonces[1], agg_pk32, msg) */
		internal static void secp256k1_musig_compute_noncehash(Span<byte> noncehash, Span<GE> aggnonce, ReadOnlySpan<byte> agg_pk32, ReadOnlySpan<byte> msg)
		{
			Span<byte> buf = stackalloc byte[33];
			using SHA256 sha = new SHA256();
			sha.InitializeTagged("MuSig/noncecoef");
			int i;
			for (i = 0; i < 2; i++)
			{
				ECPubKey.secp256k1_eckey_pubkey_serialize(buf, ref aggnonce[i], out _, true);
				sha.Write(buf);
			}
			sha.Write(agg_pk32.Slice(0, 32));
			sha.Write(msg.Slice(0, 32));
			sha.GetHash(noncehash);
		}

		public bool Verify(ECXOnlyPubKey pubKey, MusigPubNonce pubNonce, MusigPartialSignature partialSignature)
		{
			if (partialSignature == null)
				throw new ArgumentNullException(nameof(partialSignature));
			if (pubNonce == null)
				throw new ArgumentNullException(nameof(pubNonce));
			if (pubKey == null)
				throw new ArgumentNullException(nameof(pubKey));
			if (SessionCache is null)
				throw new InvalidOperationException("You need to run MusigContext.Process first");
			GEJ pkj;
			Span<GE> nonces = stackalloc GE[2];
			GEJ rj;
			GEJ tmp;
			GE pkp;
			var b = SessionCache.b;
			var pre_session = this;
			/* Compute "effective" nonce rj = nonces[0] + b*nonces[1] */
			/* TODO: use multiexp */

			nonces[0] = pubNonce.K1;
			nonces[1] = pubNonce.K2;

			rj = nonces[1].ToGroupElementJacobian();
			rj = this.ctx.EcMultContext.Mult(rj, b, null);
			rj = rj.AddVariable(nonces[0]);

			pkp = pubKey.Q;
			/* Multiplying the messagehash by the musig coefficient is equivalent
			 * to multiplying the signer's public key by the coefficient, except
			 * much easier to do. */
			var mu = ECXOnlyPubKey.secp256k1_musig_keyaggcoef(pre_session, pkp.x);
			var e = gacc * SessionCache.e * mu;

			var s = partialSignature.E;
			/* Compute -s*G + e*pkj + rj */
			s = s.Negate();
			pkj = pkp.ToGroupElementJacobian();
			tmp = ctx.EcMultContext.Mult(pkj, e, s);
			if (SessionCache.r.y.IsOdd)
			{
				rj = rj.Negate();
			}
			tmp = tmp.AddVariable(rj);
			return tmp.IsInfinity;
		}

		public bool Verify(ECPubKey pubKey, MusigPubNonce pubNonce, MusigPartialSignature partialSignature)
		{
			if (partialSignature == null)
				throw new ArgumentNullException(nameof(partialSignature));
			if (pubNonce == null)
				throw new ArgumentNullException(nameof(pubNonce));
			if (pubKey == null)
				throw new ArgumentNullException(nameof(pubKey));
			if (SessionCache is null)
				throw new InvalidOperationException("You need to run MusigContext.Process first");
			GEJ pkj;
			Span<GE> nonces = stackalloc GE[2];
			GEJ rj;
			GEJ tmp;
			GE pkp;
			var b = SessionCache.b;
			var pre_session = this;
			/* Compute "effective" nonce rj = nonces[0] + b*nonces[1] */
			/* TODO: use multiexp */

			nonces[0] = pubNonce.K1;
			nonces[1] = pubNonce.K2;

			rj = nonces[1].ToGroupElementJacobian();
			rj = this.ctx.EcMultContext.Mult(rj, b, null);
			rj = rj.AddVariable(nonces[0]);

			pkp = pubKey.ToXOnlyPubKey().Q;
			/* Multiplying the messagehash by the musig coefficient is equivalent
			 * to multiplying the signer's public key by the coefficient, except
			 * much easier to do. */
			var mu = ECXOnlyPubKey.secp256k1_musig_keyaggcoef(pre_session, pkp.x);
			var e = gacc * SessionCache.e * mu;

			var s = partialSignature.E;
			/* Compute -s*G + e*pkj + rj */
			s = s.Negate();
			pkj = pkp.ToGroupElementJacobian();
			tmp = ctx.EcMultContext.Mult(pkj, e, s);
			if (SessionCache.r.y.IsOdd)
			{
				rj = rj.Negate();
			}
			tmp = tmp.AddVariable(rj);
			return tmp.IsInfinity;
		}

		public SecpSchnorrSignature AggregateSignatures(MusigPartialSignature[] partialSignatures)
		{
			if (partialSignatures == null)
				throw new ArgumentNullException(nameof(partialSignatures));
			if (this.SessionCache is null)
				throw new InvalidOperationException("You need to run MusigContext.Process first");
			var s = Scalar.Zero;
			foreach (var sig in partialSignatures)
			{
				s = s + sig.E;
			}
			var g = pk_parity ? Scalar.MinusOne : Scalar.One;
			s = s + g * SessionCache.e * tacc;
			return new SecpSchnorrSignature(this.SessionCache.r.x, s);
		}

		public MusigPartialSignature Sign(ECPrivKey privKey, MusigPrivNonce privNonce)
		{
			if (privKey == null)
				throw new ArgumentNullException(nameof(privKey));
			if (privNonce == null)
				throw new ArgumentNullException(nameof(privNonce));
			if (privNonce.IsUsed)
				throw new ArgumentNullException(nameof(privNonce), "Nonce already used, a nonce should never be used to sign twice");
			if (SessionCache is null)
				throw new InvalidOperationException("You need to run MusigContext.Process first");

			//{
			//	/* Check in constant time if secnonce has been zeroed. */
			//	size_t i;
			//	unsigned char secnonce_acc = 0;
			//	for (i = 0; i < sizeof(*secnonce) ; i++) {
			//		secnonce_acc |= secnonce->data[i];
			//	}
			//	secp256k1_declassify(ctx, &secnonce_acc, sizeof(secnonce_acc));
			//	ARG_CHECK(secnonce_acc != 0);
			//}

			var pre_session = this;
			var session_cache = SessionCache;
			Span<Scalar> k = stackalloc Scalar[2];
			k[0] = privNonce.K1.sec;
			k[1] = privNonce.K2.sec;


			/* Obtain the signer's public key point and determine if the sk is
			 * negated before signing. That happens if if the signer's pubkey has an odd
			 * Y coordinate XOR the MuSig-combined pubkey has an odd Y coordinate XOR
			 * (if tweaked) the internal key has an odd Y coordinate.
			 *
			 * This can be seen by looking at the sk key belonging to `combined_pk`.
			 * Let's define
			 * P' := mu_0*|P_0| + ... + mu_n*|P_n| where P_i is the i-th public key
			 * point x_i*G, mu_i is the i-th musig coefficient and |.| is a function
			 * that normalizes a point to an even Y by negating if necessary similar to
			 * secp256k1_extrakeys_ge_even_y. Then we have
			 * P := |P'| + t*G where t is the tweak.
			 * And the combined xonly public key is
			 * |P| = x*G
			 *      where x = sum_i(b_i*mu_i*x_i) + b'*t
			 *            b' = -1 if P != |P|, 1 otherwise
			 *            b_i = -1 if (P_i != |P_i| XOR P' != |P'| XOR P != |P|) and 1
			 *                otherwise.
			 */

			var sk = privKey.sec;
			var pk = privKey.CreatePubKey().Q;

			pk = pk.NormalizeYVariable();

			var gacc_ = gacc;
			if (pk.y.IsOdd)
				gacc_ = gacc.Negate();
			sk = gacc_ * sk;
			/* Multiply MuSig coefficient */
			pk = pk.NormalizeXVariable();
			var mu = ECXOnlyPubKey.secp256k1_musig_keyaggcoef(pre_session, pk.x);
			sk = sk * mu;
			if (SessionCache.r.y.IsOdd)
			{
				k[0] = k[0].Negate();
				k[1] = k[1].Negate();
			}

			var e = session_cache.e * sk;
			k[1] = session_cache.b * k[1];
			k[0] = k[0] + k[1];
			e = e + k[0];
			Scalar.Clear(ref k[0]);
			Scalar.Clear(ref k[1]);
			privNonce.IsUsed = true;
			return new MusigPartialSignature(e);
		}

		public SecpSchnorrSignature Adapt(SecpSchnorrSignature signature, ECPrivKey adaptorSecret)
		{
			if (adaptorSecret == null)
				throw new ArgumentNullException(nameof(adaptorSecret));
			if (signature == null)
				throw new ArgumentNullException(nameof(signature));
			if (!processed_nonce || SessionCache is null)
				throw new InvalidOperationException("You need to run MusigContext.Process first");
			var s = signature.s;
			var t = adaptorSecret.sec;
			if (SessionCache.r.y.IsOdd)
			{
				t = t.Negate();
			}
			s = s + t;
			return new SecpSchnorrSignature(signature.rx, s);
		}

		public ECPrivKey Extract(SecpSchnorrSignature signature, MusigPartialSignature[] partialSignatures)
		{
			if (partialSignatures == null)
				throw new ArgumentNullException(nameof(partialSignatures));
			if (SessionCache is null)
				throw new InvalidOperationException("You need to run MusigContext.Process first");
			var t = signature.s;
			t = t.Negate();
			foreach (var sig in partialSignatures)
			{
				t = t + sig.E;
			}
			if (!SessionCache.r.y.IsOdd)
			{
				t = t.Negate();
			}
			return new ECPrivKey(t, this.ctx, true);
		}

		/// <summary>
		/// This function derives a random secret nonce that will be required for signing and
		/// creates a private nonce whose public part intended to be sent to other signers.
		/// </summary>
		/// <returns>A private nonce whose public part intended to be sent to other signers</returns>
		public MusigPrivNonce GenerateNonce()
		{
			return GenerateNonce(Array.Empty<byte>());
		}
		/// <summary>
		/// This function derives a secret nonce that will be required for signing and
		/// creates a private nonce whose public part intended to be sent to other signers.
		/// </summary>
		/// <param name="sessionId">A unique session_id. It is a "number used once". If null, it will be randomly generated.</param>
		/// <returns>A private nonce whose public part intended to be sent to other signers</returns>
		public MusigPrivNonce GenerateNonce(byte[]? sessionId)
		{
			return GenerateNonce(sessionId, null, null);
		}

		/// <summary>
		/// This function derives a secret nonce that will be required for signing and
		/// creates a private nonce whose public part intended to be sent to other signers.
		/// </summary>
		/// <param name="counter">A unique counter. Never reuse the same value twice for the same msg32/pubkeys.</param>
		/// <param name="signingKey">Provide the message to be signed to increase misuse-resistance. If you do provide a signingKey, sessionId32 can instead be a counter (that must never repeat!). However, it is recommended to always choose session_id32 uniformly at random. Can be null.</param>
		/// <returns>A private nonce whose public part intended to be sent to other signers</returns>
		public MusigPrivNonce GenerateNonce(ulong counter, ECPrivKey signingKey)
		{
			if (signingKey == null)
				throw new ArgumentNullException(nameof(signingKey));

			byte[] sessionId = new byte[32];
			for (int i = 0; i < 8; i++)
				sessionId[i] = (byte)(counter >> (8*i));
			return GenerateNonce(sessionId, signingKey, Array.Empty<byte>());
		}

		/// <summary>
		/// This function derives a secret nonce that will be required for signing and
		/// creates a private nonce whose public part intended to be sent to other signers.
		/// </summary>
		/// <param name="sessionId">A unique session_id32. It is a "number used once". If null, it will be randomly generated.</param>
		/// <param name="signingKey">Provide the message to be signed to increase misuse-resistance. If you do provide a signingKey, sessionId32 can instead be a counter (that must never repeat!). However, it is recommended to always choose session_id32 uniformly at random. Can be null.</param>
		/// <returns>A private nonce whose public part intended to be sent to other signers</returns>
		public MusigPrivNonce GenerateNonce(byte[]? sessionId, ECPrivKey? signingKey)
		{
			return GenerateNonce(sessionId, signingKey, Array.Empty<byte>());
		}
		/// <summary>
		/// This function derives a secret nonce that will be required for signing and
		/// creates a private nonce whose public part intended to be sent to other signers.
		/// </summary>
		/// <param name="sessionId">A unique session_id. It is a "number used once". If null, it will be randomly generated.</param>
		/// <param name="signingKey">Provide the message to be signed to increase misuse-resistance. If you do provide a signingKey, sessionId32 can instead be a counter (that must never repeat!). However, it is recommended to always choose session_id32 uniformly at random. Can be null.</param>
		/// <param name="extraInput">Provide the message to be signed to increase misuse-resistance. The extra_input32 argument can be used to provide additional data that does not repeat in normal scenarios, such as the current time. Can be null.</param>
		/// <returns>A private nonce whose public part intended to be sent to other signers</returns>
		public MusigPrivNonce GenerateNonce(byte[]? sessionId, ECPrivKey? signingKey, byte[]? extraInput)
		{
			return MusigPrivNonce.GenerateMusigNonce(ctx, sessionId, signingKey, this.msg32, this.aggregatePubKey.ToXOnlyPubKey(), extraInput);
		}
	}
}
#endif
