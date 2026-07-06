package main

// App Store receipt validation — the verifyReceipt endpoint (still the
// simplest server-side check that works for both StoreKit 1 receipts and
// consumables). Production first; Apple's 21007 means "this is a sandbox
// receipt", so we retry against the sandbox host — the recommended dance.

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"time"
)

// Outbound store/identity calls must never hang a handler goroutine — a
// degraded Apple/Google endpoint would otherwise pile up connections into a
// self-inflicted DoS.
var verifyHTTP = &http.Client{Timeout: 10 * time.Second}

// verifyAppleReceipt returns the transaction id of a purchase of sku inside
// the receipt, or an error when Apple rejects it / the sku isn't in it.
// expectedBundleID guards against the classic cross-app attack: ANY app's
// genuine receipt validates at Apple, so without pinning the bundle a cheap
// third-party app with a same-named product_id would mint our currency.
func verifyAppleReceipt(receiptB64, sku, sharedSecret, expectedBundleID string) (string, error) {
	resp, err := appleVerifyCall("https://buy.itunes.apple.com/verifyReceipt", receiptB64, sharedSecret)
	if err != nil {
		return "", err
	}
	if resp.Status == 21007 { // sandbox receipt sent to production — retry there
		resp, err = appleVerifyCall("https://sandbox.itunes.apple.com/verifyReceipt", receiptB64, sharedSecret)
		if err != nil {
			return "", err
		}
	}
	if resp.Status != 0 {
		return "", fmt.Errorf("apple status %d", resp.Status)
	}
	if expectedBundleID != "" && resp.Receipt.BundleID != expectedBundleID {
		return "", fmt.Errorf("receipt belongs to %q, not our app", resp.Receipt.BundleID)
	}
	// The newest transaction for this product wins (a consumable can appear
	// more than once; replay protection is per transaction id upstream).
	// Transaction ids are decimal strings of GROWING length — compare
	// numerically (length first), not lexicographically.
	best := ""
	newer := func(a, b string) bool { // a > b as numbers
		if len(a) != len(b) {
			return len(a) > len(b)
		}
		return a > b
	}
	for _, p := range resp.Receipt.InApp {
		if p.ProductID == sku && (best == "" || newer(p.TransactionID, best)) {
			best = p.TransactionID
		}
	}
	for _, p := range resp.LatestReceiptInfo {
		if p.ProductID == sku && (best == "" || newer(p.TransactionID, best)) {
			best = p.TransactionID
		}
	}
	if best == "" {
		return "", errors.New("sku not present in the receipt")
	}
	return best, nil
}

type appleVerifyResponse struct {
	Status  int `json:"status"`
	Receipt struct {
		BundleID string       `json:"bundle_id"`
		InApp    []appleInApp `json:"in_app"`
	} `json:"receipt"`
	LatestReceiptInfo []appleInApp `json:"latest_receipt_info"`
}

type appleInApp struct {
	ProductID     string `json:"product_id"`
	TransactionID string `json:"transaction_id"`
}

func appleVerifyCall(endpoint, receiptB64, sharedSecret string) (*appleVerifyResponse, error) {
	body, _ := json.Marshal(map[string]string{
		"receipt-data": receiptB64,
		"password":     sharedSecret,
	})
	resp, err := verifyHTTP.Post(endpoint, "application/json", bytes.NewReader(body))
	if err != nil {
		return nil, fmt.Errorf("apple verifyReceipt unreachable: %w", err)
	}
	defer resp.Body.Close()
	var out appleVerifyResponse
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		return nil, errors.New("apple verifyReceipt: bad response")
	}
	return &out, nil
}
