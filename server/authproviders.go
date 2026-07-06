package main

// Platform identity verification — the real checks behind /v1/auth/link and
// /v1/auth/login. Google id_tokens go through the tokeninfo endpoint (one
// HTTPS call, Google validates the signature for us); Apple identity tokens
// are RS256 JWTs verified locally against Apple's published JWKS (cached).
// The "dev" provider (any token, subject = the token) exists only behind
// -auth-dev for local builds and tests — production rejects it.

import (
	"crypto"
	"crypto/rsa"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"math/big"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

func (s *AuthService) verifyProviderReal(provider, token string) (string, error) {
	switch provider {
	case "dev":
		if !s.AuthDev {
			return "", errors.New("dev provider disabled (run with -auth-dev)")
		}
		if len(token) < 4 {
			return "", errors.New("dev token too short")
		}
		return token, nil
	case "google":
		return verifyGoogleIDToken(token, s.GoogleClientID)
	case "apple":
		return verifyAppleIdentityToken(token, s.AppleBundleID)
	default:
		return "", fmt.Errorf("unknown provider %q", provider)
	}
}

// ── Google ──────────────────────────────────────────────────────────────────

// verifyGoogleIDToken asks Google's tokeninfo endpoint to validate the id_token
// (signature, expiry) and returns the stable subject. aud is pinned when a
// client id is configured.
func verifyGoogleIDToken(idToken, clientID string) (string, error) {
	resp, err := http.Get("https://oauth2.googleapis.com/tokeninfo?id_token=" + url.QueryEscape(idToken))
	if err != nil {
		return "", fmt.Errorf("google tokeninfo unreachable: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != 200 {
		return "", fmt.Errorf("google rejected the token (%d)", resp.StatusCode)
	}
	var info struct {
		Sub string `json:"sub"`
		Aud string `json:"aud"`
	}
	if err := json.NewDecoder(resp.Body).Decode(&info); err != nil || info.Sub == "" {
		return "", errors.New("google tokeninfo: bad response")
	}
	if clientID != "" && info.Aud != clientID {
		return "", errors.New("google token audience mismatch")
	}
	return info.Sub, nil
}

// ── Apple ───────────────────────────────────────────────────────────────────

// Apple's JWKS, fetched lazily and cached; Sign in with Apple identity tokens
// are RS256 JWTs signed by one of these keys.
var appleKeys struct {
	mu      sync.Mutex
	byKid   map[string]*rsa.PublicKey
	fetched time.Time
}

func verifyAppleIdentityToken(jwt, bundleID string) (string, error) {
	parts := strings.Split(jwt, ".")
	if len(parts) != 3 {
		return "", errors.New("not a JWT")
	}
	headJSON, err := base64.RawURLEncoding.DecodeString(parts[0])
	if err != nil {
		return "", errors.New("bad JWT header")
	}
	var head struct {
		Kid string `json:"kid"`
		Alg string `json:"alg"`
	}
	if json.Unmarshal(headJSON, &head) != nil || head.Alg != "RS256" || head.Kid == "" {
		return "", errors.New("unsupported JWT header")
	}
	key, err := appleKeyFor(head.Kid)
	if err != nil {
		return "", err
	}
	sig, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil {
		return "", errors.New("bad JWT signature encoding")
	}
	digest := sha256.Sum256([]byte(parts[0] + "." + parts[1]))
	if rsa.VerifyPKCS1v15(key, crypto.SHA256, digest[:], sig) != nil {
		return "", errors.New("apple token signature invalid")
	}

	claimsJSON, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return "", errors.New("bad JWT claims")
	}
	var claims struct {
		Iss string `json:"iss"`
		Sub string `json:"sub"`
		Aud string `json:"aud"`
		Exp int64  `json:"exp"`
	}
	if json.Unmarshal(claimsJSON, &claims) != nil || claims.Sub == "" {
		return "", errors.New("bad apple claims")
	}
	if claims.Iss != "https://appleid.apple.com" {
		return "", errors.New("apple token issuer mismatch")
	}
	if claims.Exp > 0 && time.Now().Unix() > claims.Exp {
		return "", errors.New("apple token expired")
	}
	if bundleID != "" && claims.Aud != bundleID {
		return "", errors.New("apple token audience mismatch")
	}
	return claims.Sub, nil
}

func appleKeyFor(kid string) (*rsa.PublicKey, error) {
	appleKeys.mu.Lock()
	defer appleKeys.mu.Unlock()
	if k, ok := appleKeys.byKid[kid]; ok {
		return k, nil
	}
	// Unknown kid (rotation) or first use — refresh, but not more than once a
	// minute so a bad token can't hammer Apple through us.
	if time.Since(appleKeys.fetched) < time.Minute && appleKeys.byKid != nil {
		return nil, errors.New("unknown apple key id")
	}
	resp, err := http.Get("https://appleid.apple.com/auth/keys")
	if err != nil {
		return nil, fmt.Errorf("apple JWKS unreachable: %w", err)
	}
	defer resp.Body.Close()
	var doc struct {
		Keys []struct {
			Kid string `json:"kid"`
			N   string `json:"n"`
			E   string `json:"e"`
		} `json:"keys"`
	}
	if json.NewDecoder(resp.Body).Decode(&doc) != nil {
		return nil, errors.New("apple JWKS: bad response")
	}
	appleKeys.byKid = map[string]*rsa.PublicKey{}
	appleKeys.fetched = time.Now()
	for _, k := range doc.Keys {
		nb, err1 := base64.RawURLEncoding.DecodeString(k.N)
		eb, err2 := base64.RawURLEncoding.DecodeString(k.E)
		if err1 != nil || err2 != nil {
			continue
		}
		appleKeys.byKid[k.Kid] = &rsa.PublicKey{
			N: new(big.Int).SetBytes(nb),
			E: int(new(big.Int).SetBytes(eb).Int64()),
		}
	}
	if k, ok := appleKeys.byKid[kid]; ok {
		return k, nil
	}
	return nil, errors.New("unknown apple key id")
}
