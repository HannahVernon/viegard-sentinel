(() => {
    "use strict";

    const tokenName = "__RequestVerificationToken";
    const tokenHeader = "RequestVerificationToken";

    function getToken(element) {
        const form = element.closest("form") || document;
        const input = form.querySelector(`input[name="${tokenName}"]`)
            || document.querySelector(`input[name="${tokenName}"]`);
        return input ? input.value : "";
    }

    function showMessage(element, message, isError = false) {
        const targetSelector = element.getAttribute("data-webauthn-message");
        const target = targetSelector ? document.querySelector(targetSelector) : null;
        if (!target) {
            return;
        }

        target.textContent = message;
        target.classList.toggle("error", isError);
        target.hidden = false;
    }

    function base64UrlToBuffer(value) {
        const padded = value.replace(/-/g, "+").replace(/_/g, "/")
            .padEnd(Math.ceil(value.length / 4) * 4, "=");
        const binary = atob(padded);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i += 1) {
            bytes[i] = binary.charCodeAt(i);
        }

        return bytes.buffer;
    }

    function bufferToBase64Url(buffer) {
        const bytes = new Uint8Array(buffer);
        let binary = "";
        for (let i = 0; i < bytes.length; i += 0x8000) {
            binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
        }

        return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/g, "");
    }

    function prepareCreateOptions(options) {
        return {
            ...options,
            challenge: base64UrlToBuffer(options.challenge),
            user: {
                ...options.user,
                id: base64UrlToBuffer(options.user.id),
            },
            excludeCredentials: (options.excludeCredentials || []).map(credential => ({
                ...credential,
                id: base64UrlToBuffer(credential.id),
            })),
        };
    }

    function prepareGetOptions(options) {
        return {
            ...options,
            challenge: base64UrlToBuffer(options.challenge),
            allowCredentials: (options.allowCredentials || []).map(credential => ({
                ...credential,
                id: base64UrlToBuffer(credential.id),
            })),
        };
    }

    function credentialToJson(credential) {
        if (!credential) {
            return null;
        }

        const response = credential.response;
        const json = {
            id: credential.id,
            rawId: bufferToBase64Url(credential.rawId),
            type: credential.type,
            clientExtensionResults: credential.getClientExtensionResults(),
            response: {},
        };

        if (response.attestationObject) {
            json.response.attestationObject = bufferToBase64Url(response.attestationObject);
            json.response.clientDataJSON = bufferToBase64Url(response.clientDataJSON);
            json.response.transports = typeof response.getTransports === "function"
                ? response.getTransports()
                : [];
        } else {
            json.response.authenticatorData = bufferToBase64Url(response.authenticatorData);
            json.response.clientDataJSON = bufferToBase64Url(response.clientDataJSON);
            json.response.signature = bufferToBase64Url(response.signature);
            json.response.userHandle = response.userHandle ? bufferToBase64Url(response.userHandle) : null;
        }

        return json;
    }

    async function fetchOptions(url, token, button) {
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                Accept: "application/json",
                [tokenHeader]: token,
            },
        });

        if (response.status === 204) {
            showMessage(button, "No enrolled security keys are available for this account.", true);
            return null;
        }

        if (!response.ok) {
            await handleResponse(response, button);
            return null;
        }

        return response.json();
    }

    async function postJson(url, token, body, button) {
        const response = await fetch(url, {
            method: "POST",
            credentials: "same-origin",
            headers: {
                Accept: "application/json",
                "Content-Type": "application/json",
                [tokenHeader]: token,
            },
            body: JSON.stringify(body),
        });

        await handleResponse(response, button);
    }

    async function handleResponse(response, button) {
        if (response.redirected) {
            window.location.assign(response.url);
            return;
        }

        const contentType = response.headers.get("content-type") || "";
        if (contentType.includes("application/json")) {
            const payload = await response.json();
            if (payload.redirect) {
                window.location.assign(payload.redirect);
                return;
            }

            if (payload.error) {
                showMessage(button, payload.error, true);
                return;
            }
        }

        if (!response.ok) {
            showMessage(button, "Security-key operation was not accepted.", true);
        }
    }

    async function register(button) {
        if (!window.PublicKeyCredential || !navigator.credentials) {
            showMessage(button, "This browser does not expose WebAuthn credentials.", true);
            return;
        }

        const token = getToken(button);
        const options = await fetchOptions("/auth/webauthn/register/options", token, button);
        if (!options) {
            return;
        }

        const credential = await navigator.credentials.create({ publicKey: prepareCreateOptions(options) });
        const nameSelector = button.getAttribute("data-webauthn-name");
        const nameInput = nameSelector ? document.querySelector(nameSelector) : null;
        await postJson("/auth/webauthn/register", token, {
            name: nameInput ? nameInput.value : "",
            response: credentialToJson(credential),
        }, button);
    }

    async function assertCredential(button) {
        if (!window.PublicKeyCredential || !navigator.credentials) {
            showMessage(button, "This browser does not expose WebAuthn credentials.", true);
            return;
        }

        const token = getToken(button);
        const options = await fetchOptions("/auth/webauthn/assert/options", token, button);
        if (!options) {
            return;
        }

        const credential = await navigator.credentials.get({ publicKey: prepareGetOptions(options) });
        await postJson("/auth/webauthn/assert", token, credentialToJson(credential), button);
    }

    document.addEventListener("DOMContentLoaded", () => {
        document.querySelectorAll("[data-webauthn-register]").forEach(button => {
            button.addEventListener("click", event => {
                event.preventDefault();
                register(button).catch(() => showMessage(button, "Security-key enrollment failed.", true));
            });
        });

        document.querySelectorAll("[data-webauthn-assert]").forEach(button => {
            button.addEventListener("click", event => {
                event.preventDefault();
                assertCredential(button).catch(() => showMessage(button, "Security-key verification failed.", true));
            });
        });
    });
})();
