package main

import (
	"context"
	"crypto/tls"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"math/rand"
	"net/http"
	"os"
	"os/signal"
	"sync"
	"sync/atomic"
	"syscall"
	"time"

	"github.com/gorilla/websocket"
)

type metrics struct {
	connected       atomic.Int64
	connectFailed   atomic.Int64
	disconnected    atomic.Int64
	received        atomic.Int64
	receiveErrors   atomic.Int64
	posted          atomic.Int64
	postOK          atomic.Int64
	postFailed      atomic.Int64
	lastNo          atomic.Int64
	latencySamples  atomic.Int64
	latencyTotalMS  atomic.Int64
	latencyMaxMS    atomic.Int64
}

func (m *metrics) resetMeasurement() {
	m.received.Store(0)
	m.receiveErrors.Store(0)
	m.posted.Store(0)
	m.postOK.Store(0)
	m.lastNo.Store(0)
	m.latencySamples.Store(0)
	m.latencyTotalMS.Store(0)
	m.latencyMaxMS.Store(0)
}

type chatEnvelope struct {
	Chat *struct {
		No       int64  `json:"no"`
		Date     int64  `json:"date"`
		DateUsec int64  `json:"date_usec"`
		Content  string `json:"content"`
	} `json:"chat"`
}

type watchMessage struct {
	Type string `json:"type"`
	Data struct {
		Code            string `json:"code"`
		KeepIntervalSec int    `json:"keepIntervalSec"`
	} `json:"data"`
}

func main() {
	var commentURL string
	var watchURL string
	var clients int
	var duration time.Duration
	var postRate float64
	var postText string
	var ramp time.Duration
	var reportInterval time.Duration
	var insecure bool
	var startAfterConnected bool
	var warmup time.Duration

	flag.StringVar(&commentURL, "url", "", "comment WebSocket URL, for example ws://host:5000/comment/jk104")
	flag.StringVar(&watchURL, "watch-url", "", "watch WebSocket URL used for posting, for example ws://host:5000/watch/jk104")
	flag.IntVar(&clients, "clients", 100, "number of /comment clients")
	flag.DurationVar(&duration, "duration", 60*time.Second, "test duration, for example 60s or 5m")
	flag.Float64Var(&postRate, "post-rate", 0, "average comments per second per client to post through --watch-url")
	flag.StringVar(&postText, "post-text", "load test", "posted comment text prefix")
	flag.DurationVar(&ramp, "ramp", 10*time.Second, "time used to spread client connection attempts")
	flag.DurationVar(&reportInterval, "report-interval", 5*time.Second, "progress report interval")
	flag.BoolVar(&insecure, "insecure", false, "skip TLS certificate verification for wss URLs")
	flag.BoolVar(&startAfterConnected, "start-after-connected", false, "start posting and measurement after all comment clients connected or failed")
	flag.DurationVar(&warmup, "warmup", 0, "wait time after all clients connected before measurement starts")
	flag.Parse()

	if commentURL == "" {
		log.Fatal("--url is required")
	}
	if clients < 0 {
		log.Fatal("--clients must be >= 0")
	}
	if postRate > 0 && watchURL == "" {
		log.Fatal("--watch-url is required when --post-rate is greater than 0")
	}
	if len([]rune(postText)) > 60 {
		log.Fatal("--post-text must be 60 characters or shorter so the sequence suffix fits in 75 characters")
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	runCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	if !startAfterConnected {
		runCtx, cancel = context.WithTimeout(ctx, duration)
		defer cancel()
	}

	dialer := websocket.Dialer{
		HandshakeTimeout: 10 * time.Second,
		Subprotocols:    []string{"msg.nicovideo.jp#json"},
		TLSClientConfig:  &tls.Config{InsecureSkipVerify: insecure}, //nolint:gosec
	}
	watchDialer := websocket.Dialer{
		HandshakeTimeout: 10 * time.Second,
		TLSClientConfig:  &tls.Config{InsecureSkipVerify: insecure}, //nolint:gosec
	}

	var m metrics
	started := time.Now()
	var wg sync.WaitGroup
	connectResults := make(chan struct{}, clients)
	startPosting := make(chan struct{})

	for i := 0; i < clients; i++ {
		delay := time.Duration(0)
		if clients > 1 && ramp > 0 {
			delay = time.Duration(float64(ramp) * float64(i) / float64(clients-1))
		}
		wg.Add(1)
		go func(id int, d time.Duration) {
			defer wg.Done()
			select {
			case <-runCtx.Done():
				return
			case <-time.After(d):
			}
			runClient(runCtx, id, commentURL, watchURL, dialer, watchDialer, postRate, postText, &m, connectResults, startPosting)
		}(i, delay)
	}

	ticker := time.NewTicker(reportInterval)
	defer ticker.Stop()

	if startAfterConnected {
		waitForClientConnections(ctx, clients, connectResults, &m, started, ticker)
		if ctx.Err() != nil {
			cancel()
		} else if warmup > 0 {
			waitWarmup(ctx, warmup, &m, started, ticker)
		}
		m.resetMeasurement()
		started = time.Now()
		go func() {
			timer := time.NewTimer(duration)
			defer timer.Stop()
			select {
			case <-ctx.Done():
				cancel()
			case <-timer.C:
				cancel()
			}
		}()
		close(startPosting)
	} else {
		close(startPosting)
	}

	done := make(chan struct{})
	go func() {
		wg.Wait()
		close(done)
	}()

	for {
		select {
		case <-ticker.C:
			printReport(time.Since(started), &m, false)
		case <-done:
			printReport(time.Since(started), &m, true)
			return
		}
	}
}

func waitForClientConnections(ctx context.Context, clients int, connectResults <-chan struct{}, m *metrics, started time.Time, ticker *time.Ticker) {
	for completed := 0; completed < clients; {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			printReport(time.Since(started), m, false)
		case <-connectResults:
			completed++
		}
	}
}

func waitWarmup(ctx context.Context, warmup time.Duration, m *metrics, started time.Time, ticker *time.Ticker) {
	timer := time.NewTimer(warmup)
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			printReport(time.Since(started), m, false)
		case <-timer.C:
			return
		}
	}
}

func runClient(
	ctx context.Context,
	id int,
	commentURL string,
	watchURL string,
	commentDialer websocket.Dialer,
	watchDialer websocket.Dialer,
	postRate float64,
	postText string,
	m *metrics,
	connectResults chan<- struct{},
	startPosting <-chan struct{},
) {
	conn, resp, err := commentDialer.DialContext(ctx, commentURL, http.Header{})
	if err != nil {
		m.connectFailed.Add(1)
		connectResults <- struct{}{}
		if resp != nil {
			log.Printf("client=%d connect failed: status=%s err=%v", id, resp.Status, err)
		}
		return
	}
	defer conn.Close()
	clientCtx, clientCancel := context.WithCancel(ctx)
	m.connected.Add(1)
	defer m.disconnected.Add(1)

	var posterDone chan struct{}
	if postRate > 0 {
		posterReady := make(chan bool, 1)
		posterDone = make(chan struct{})
		go func() {
			defer close(posterDone)
			runPoster(clientCtx, id, watchURL, watchDialer, postRate, postText, m, startPosting, posterReady)
		}()
		<-posterReady
		defer func() {
			<-posterDone
		}()
	}
	defer clientCancel()
	connectResults <- struct{}{}

	go func() {
		<-clientCtx.Done()
		_ = conn.Close()
	}()

	for {
		_, data, err := conn.ReadMessage()
		if err != nil {
			if ctx.Err() == nil {
				m.receiveErrors.Add(1)
			}
			return
		}
		m.received.Add(1)
		recordChatMetrics(data, m)
	}
}

func runPoster(
	ctx context.Context,
	id int,
	url string,
	dialer websocket.Dialer,
	rate float64,
	textPrefix string,
	m *metrics,
	startPosting <-chan struct{},
	ready chan<- bool,
) {
	readySent := false
	sendReady := func(ok bool) {
		if readySent {
			return
		}
		readySent = true
		ready <- ok
	}

	conn, _, err := dialer.DialContext(ctx, url, http.Header{})
	if err != nil {
		log.Printf("poster client=%d connect failed: %v", id, err)
		m.postFailed.Add(1)
		sendReady(false)
		return
	}
	defer conn.Close()
	var writeMu sync.Mutex
	writeJSON := func(v any) error {
		writeMu.Lock()
		defer writeMu.Unlock()
		return conn.WriteJSON(v)
	}
	go func() {
		<-ctx.Done()
		_ = conn.Close()
	}()

	if err := writeJSON(map[string]any{
		"data": map[string]any{
			"room": map[string]any{"commentable": true},
		},
	}); err != nil {
		log.Printf("poster client=%d startWatching failed: %v", id, err)
		m.postFailed.Add(1)
		sendReady(false)
		return
	}

	keepIntervalSec, err := waitWatchRoom(ctx, conn)
	if err != nil {
		log.Printf("poster client=%d watch handshake failed: %v", id, err)
		m.postFailed.Add(1)
		sendReady(false)
		return
	}
	if keepIntervalSec > 0 {
		go runWatchKeepSeat(ctx, keepIntervalSec, writeJSON)
	}
	sendReady(true)

	select {
	case <-ctx.Done():
		return
	case <-startPosting:
	}

	interval := time.Duration(float64(time.Second) / rate)
	if interval <= 0 {
		interval = time.Millisecond
	}
	rng := rand.New(rand.NewSource(time.Now().UnixNano() + int64(id)*7919))
	initialDelay := time.Duration(rng.Float64() * float64(interval))
	timer := time.NewTimer(initialDelay)
	select {
	case <-ctx.Done():
		timer.Stop()
		return
	case <-timer.C:
	}

	ticker := time.NewTicker(interval)
	defer ticker.Stop()

	seq := int64(0)
	for {
		select {
		case <-ctx.Done():
			return
		default:
		}

		seq++
		text := fmt.Sprintf("%s c%d %d", textPrefix, id, seq)
		if len([]rune(text)) > 75 {
			text = string([]rune(text)[:75])
		}
		if err := writeJSON(map[string]any{
			"type": "postComment",
			"data": map[string]any{
				"text":     text,
				"vpos":     0,
				"color":    "white",
				"size":     "medium",
				"position": "naka",
				"font":     "defont",
			},
		}); err != nil {
			log.Printf("post client=%d write failed: %v", id, err)
			m.postFailed.Add(1)
			return
		}
		m.posted.Add(1)
		_, data, err := conn.ReadMessage()
		if err != nil {
			log.Printf("post client=%d response read failed: %v", id, err)
			m.postFailed.Add(1)
			return
		}
		var msg watchMessage
		if json.Unmarshal(data, &msg) == nil && msg.Type == "postCommentResult" {
			m.postOK.Add(1)
		} else {
			m.postFailed.Add(1)
		}

		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func runWatchKeepSeat(ctx context.Context, keepIntervalSec int, writeJSON func(any) error) {
	if keepIntervalSec < 5 {
		keepIntervalSec = 5
	}
	ticker := time.NewTicker(time.Duration(keepIntervalSec) * time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			_ = writeJSON(map[string]any{"type": "keepSeat"})
		}
	}
}

func waitWatchRoom(ctx context.Context, conn *websocket.Conn) (int, error) {
	deadline := time.Now().Add(10 * time.Second)
	if ctxDeadline, ok := ctx.Deadline(); ok && ctxDeadline.Before(deadline) {
		deadline = ctxDeadline
	}
	_ = conn.SetReadDeadline(deadline)
	defer conn.SetReadDeadline(time.Time{})

	keepIntervalSec := 0
	for {
		_, data, err := conn.ReadMessage()
		if err != nil {
			return 0, err
		}
		var msg watchMessage
		if json.Unmarshal(data, &msg) != nil {
			continue
		}
		switch msg.Type {
		case "seat":
			keepIntervalSec = msg.Data.KeepIntervalSec
		case "room":
			return keepIntervalSec, nil
		case "error":
			return 0, fmt.Errorf("watch error: %s", msg.Data.Code)
		}
	}
}

func recordChatMetrics(data []byte, m *metrics) {
	var env chatEnvelope
	if json.Unmarshal(data, &env) != nil || env.Chat == nil {
		return
	}
	for {
		current := m.lastNo.Load()
		if env.Chat.No <= current || m.lastNo.CompareAndSwap(current, env.Chat.No) {
			break
		}
	}
	if env.Chat.Date > 0 {
		latency := time.Since(time.Unix(env.Chat.Date, env.Chat.DateUsec*1000)).Milliseconds()
		if latency >= 0 && latency < int64((24*time.Hour)/time.Millisecond) {
			m.latencySamples.Add(1)
			m.latencyTotalMS.Add(latency)
			for {
				current := m.latencyMaxMS.Load()
				if latency <= current || m.latencyMaxMS.CompareAndSwap(current, latency) {
					break
				}
			}
		}
	}
}

func printReport(elapsed time.Duration, m *metrics, final bool) {
	samples := m.latencySamples.Load()
	avgLatency := int64(0)
	if samples > 0 {
		avgLatency = m.latencyTotalMS.Load() / samples
	}
	prefix := "progress"
	if final {
		prefix = "final"
	}
	fmt.Printf(
		"%s elapsed=%s connected=%d connectFailed=%d disconnected=%d received=%d recvErr=%d posted=%d postOK=%d postFailed=%d lastNo=%d latencyAvgMs=%d latencyMaxMs=%d\n",
		prefix,
		elapsed.Round(time.Second),
		m.connected.Load(),
		m.connectFailed.Load(),
		m.disconnected.Load(),
		m.received.Load(),
		m.receiveErrors.Load(),
		m.posted.Load(),
		m.postOK.Load(),
		m.postFailed.Load(),
		m.lastNo.Load(),
		avgLatency,
		m.latencyMaxMS.Load(),
	)
}
