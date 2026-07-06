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
)

// verifyAppleReceipt returns the transaction id of a purchase of sku inside
// the receipt, or an error when Apple rejects it / the sku isn't in it.
func verifyAppleReceipt(receiptB64, sku, sharedSecret string) (string, error) {
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
	// The newest transaction for this product wins (a consumable can appear
	// more than once; replay protection is per transaction id upstream).
	best := ""
	for _, p := range resp.Receipt.InApp {
		if p.ProductID == sku && p.TransactionID > best {
			best = p.TransactionID
		}
	}
	for _, p := range resp.LatestReceiptInfo {
		if p.ProductID == sku && p.TransactionID > best {
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
		InApp []appleInApp `json:"in_app"`
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
	resp, err := http.Post(endpoint, "application/json", bytes.NewReader(body))
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
