FROM golang:1.22-alpine AS builder

WORKDIR /usr/src/app

RUN apk add --no-cache git

COPY go.mod go.sum ./

RUN go mod download && go mod verify

COPY . .

RUN CGO_ENABLED=0 GOOS=linux GOARCH=amd64 go build -ldflags="-w -s" -o /usr/local/bin/main .

FROM alpine:3

COPY --from=builder /usr/local/bin/main /usr/local/bin/main

CMD ["/usr/local/bin/main"]