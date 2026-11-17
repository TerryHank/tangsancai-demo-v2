class MotionCaptureEngine {
    constructor({ onFrame, onStatus }) {
        this.onFrame = onFrame;
        this.onStatus = onStatus;
        this.isPaused = false;
        this.isPoseTriggerMode = false;
        this.activePoseKey = 'optimal_1';
        this.isAbsent = true;
        this.resetActivity();
    }

    resetActivity() {
        this.matchStartTime = null;
        this.spinState = 0;
        this.shoulderBaseline = null;
        this.stillStartTime = null;
        this.isStill = false;
        this.previous = null;
        this.speed = null;
    }

    start() {
        if (this.socket) return;
        const url = new URL('/kinect', location.href);
        url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        const socket = new WebSocket(url);
        socket.binaryType = 'arraybuffer';
        this.socket = socket;
        socket.addEventListener('message', ({ data }) => {
            const bytes = new DataView(data);
            const headerLength = bytes.getUint32(0, true);
            const header = JSON.parse(new TextDecoder().decode(new Uint8Array(data, 4, headerLength)));
            if (header.state !== 'frame') {
                this.markAbsent();
                this.onStatus(header.state, header.device, header.detail);
                return;
            }
            this.onStatus('connected', header.device);
            const offset = 4 + headerLength;
            this.processFrame(header, data.byteLength > offset ? new Blob([new Uint8Array(data, offset)], { type: 'image/jpeg' }) : null);
        });
        socket.addEventListener('close', () => {
            if (this.socket !== socket) return;
            this.socket = null;
            this.markAbsent();
            this.onStatus('waiting', 'none');
            this.reconnect = setTimeout(() => this.start(), 1000);
        });
        socket.addEventListener('error', () => socket.close());
    }

    stop() {
        clearTimeout(this.reconnect);
        const socket = this.socket;
        this.socket = null;
        socket?.close();
        this.markAbsent();
    }

    pause() { this.isPaused = true; }
    resume() {
        if (this.isPaused) this.resetActivity();
        this.isPaused = false;
    }

    markAbsent() {
        if (!this.isAbsent) window.onUserStill?.(true);
        this.isAbsent = true;
        this.bodyId = null;
        this.missingSince = null;
        this.resetActivity();
    }

    processFrame(frame, image) {
        if (this.isPaused) {
            if (image) this.onFrame(frame, image);
            return;
        }
        const now = frame.timestampMs;
        if (frame.bodyId == null) {
            if (image) this.onFrame(frame, image);
            this.missingSince ??= now;
            this.matchStartTime = null;
            if (now - this.missingSince >= 5000) this.markAbsent();
            return;
        }
        if (frame.bodyId !== this.bodyId) {
            this.markAbsent();
            this.bodyId = frame.bodyId;
        }
        this.isAbsent = false;
        this.missingSince = null;
        if (image) this.onFrame(frame, image);
        window.onUserDetected?.();
        this.checkActivity(frame.joints, now);
        if (this.isPoseTriggerMode) this.checkGesture(frame.joints, now);
    }

    checkGesture(joints, now) {
        const changeMaterial = this.activePoseKey === 'spread_arms';
        const raised = ['left', 'right'].some(side => {
            const shoulder = joints[side + 'Shoulder'];
            const wrist = joints[side + 'Wrist'];
            const hip = joints[side + 'Hip'];
            if (!shoulder?.tracked || !wrist?.tracked || (changeMaterial && !hip?.tracked)) return false;
            const level = changeMaterial ? (shoulder.y + hip.y) / 2 : shoulder.y;
            return wrist.y < level;
        });
        if (!raised) {
            this.matchStartTime = null;
            return;
        }
        this.matchStartTime ??= now;
        const hold = changeMaterial ? 700 : 800;
        const elapsed = now - this.matchStartTime;
        this.onPoseScore?.(Math.min(100, Math.round(elapsed * 100 / hold)));
        if (elapsed < hold) return;
        this.matchStartTime = null;
        this.isPoseTriggerMode = false;
        this.onPoseSuccess?.(this.activePoseKey);
    }

    checkActivity(joints, now) {
        const left = joints.leftShoulder;
        const right = joints.rightShoulder;
        if (left?.tracked && right?.tracked) {
            const x = left.x - right.x;
            const width = Math.hypot(x, left.z - right.z);
            const facing = width > 0 ? x / width : 0;
            if (this.shoulderBaseline == null && Math.abs(facing) >= 0.58) this.shoulderBaseline = Math.sign(facing);
            const direction = facing * (this.shoulderBaseline ?? 0);
            if (this.spinState && now - this.spinStarted > 2500) {
                this.spinState = 0;
            } else if (!this.spinState && this.shoulderBaseline && Math.abs(direction) < 0.38) {
                this.spinState = 1;
                this.spinStarted = now;
            } else if (this.spinState === 1 && direction < -0.58) {
                this.spinState = 2;
            } else if (this.spinState === 2 && Math.abs(direction) < 0.38) {
                this.spinState = 3;
            } else if (this.spinState === 3 && direction > 0.58) {
                this.spinState = 0;
                window.onUserSpin?.();
            }
        }

        const current = { joints, now };
        const previous = this.previous;
        this.previous = current;
        if (!previous || now <= previous.now || now - previous.now > 500) {
            this.stillStartTime = null;
            this.speed = null;
            return;
        }
        let distances = 0;
        let count = 0;
        for (const [name, joint] of Object.entries(joints)) {
            const prior = previous.joints[name];
            if (!joint.tracked || !prior?.tracked) continue;
            distances += Math.hypot(joint.x - prior.x, joint.y - prior.y, joint.z - prior.z);
            count++;
        }
        if (!count) {
            this.stillStartTime = null;
            this.speed = null;
            return;
        }
        const speed = distances / count * 1000 / (now - previous.now);
        this.speed = this.speed === null ? speed : this.speed * 0.9 + speed * 0.1;
        if (this.speed >= 0.07) {
            this.stillStartTime = null;
            this.isStill = false;
        } else {
            this.stillStartTime ??= now;
            if (!this.isStill && now - this.stillStartTime >= 5000) {
                this.isStill = true;
                window.onUserStill?.(false);
            }
        }
    }
}
