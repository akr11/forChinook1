const samples = [
  ["Старт", "B:W21,22,23,24,25,26,27,28,29,30,31,32:B1,2,3,4,5,6,7,8,9,10,11,12"],
  ["Приклад з PDF", "B:W18,19,22,25,27,28,30,32:B1,5,6,7,10,12,14,16"],
  ["Взяття 22x15", "W:W22:B18"],
  ["Два королі", "W:WK32:BK1"],
  ["4 королі проти 1", "B:WK1:BK29,BK30,BK31,BK32"],
  ["Ендшпіль 7", "B:WK29,WK30,WK31,WK32:BK1,BK2,BK3"]
];

const boardEl = document.querySelector("#board");
const statusEl = document.querySelector("#status");
const outputEl = document.querySelector("#output");
const sampleEl = document.querySelector("#samples");
const levelEl = document.querySelector("#level");

let position = samples[0][1];
let legal = [];
let selected = null;

for (const [name] of samples) {
  const option = document.createElement("option");
  option.textContent = name;
  sampleEl.append(option);
}

sampleEl.addEventListener("change", () => {
  position = samples[sampleEl.selectedIndex][1];
  selected = null;
  refresh();
});

document.querySelector("#suggest").addEventListener("click", suggest);
document.querySelector("#swap").addEventListener("click", () => {
  position = position.startsWith("W:") ? "B" + position.slice(1) : "W" + position.slice(1);
  selected = null;
  refresh();
});

function coord(index) {
  const row = Math.floor(index / 4);
  const col = (index % 4) * 2 + (row % 2 === 0 ? 1 : 0);
  return { row, col };
}

function parse(pdn) {
  const parts = pdn.split(":");
  const white = parts[1].slice(1);
  const black = parts[2].slice(1);
  const squares = Array(32).fill(null);
  const put = (list, color) => {
    if (!list) return;
    for (const token of list.split(",").filter(Boolean)) {
      const king = token.startsWith("K");
      const number = Number(king ? token.slice(1) : token);
      squares[number - 1] = { color, king };
    }
  };
  put(white, "white");
  put(black, "black");
  return { side: parts[0], squares };
}

function render() {
  const { side, squares } = parse(position);
  boardEl.replaceChildren();
  const targets = new Set();
  if (selected != null) {
    for (const move of legal) {
      const bits = move.split(/[-x]/);
      if (Number(bits[0]) === selected + 1)
        targets.add(Number(bits.at(-1)) - 1);
    }
  }

  for (let row = 0; row < 8; row++) {
    for (let col = 0; col < 8; col++) {
      const cell = document.createElement("div");
      const dark = row % 2 === 0 ? col % 2 === 1 : col % 2 === 0;
      cell.className = "square " + (dark ? "dark" : "light");
      if (!dark) {
        boardEl.append(cell);
        continue;
      }
      const index = row * 4 + Math.floor(col / 2);
      const label = document.createElement("span");
      label.textContent = String(index + 1);
      cell.append(label);
      if (index === selected) cell.classList.add("selected");
      if (targets.has(index)) cell.classList.add("target");
      const piece = squares[index];
      if (piece) {
        const disk = document.createElement("div");
        disk.className = "piece " + piece.color + (piece.king ? " king" : "");
        cell.append(disk);
      }
      cell.addEventListener("click", () => onSquare(index));
      boardEl.append(cell);
    }
  }
  statusEl.textContent = (side === "W" ? "Хід білих" : "Хід чорних") + ` · легальних ходів: ${legal.length}`;
}

async function refresh() {
  const response = await fetch("/v1/moves", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ position })
  });
  const body = await response.json();
  if (!response.ok) {
    legal = [];
    outputEl.textContent = JSON.stringify(body, null, 2);
    statusEl.textContent = "Позиція відхилена";
    render();
    return;
  }
  legal = body.moves;
  render();
}

async function onSquare(index) {
  const move = legal.find((item) => {
    const bits = item.split(/[-x]/);
    return Number(bits[0]) === selected + 1 && Number(bits.at(-1)) === index + 1;
  });
  if (selected != null && move) {
    await play(move);
    return;
  }
  const owner = parse(position).squares[index];
  const side = position.startsWith("W:") ? "white" : "black";
  selected = owner && owner.color === side ? index : null;
  render();
}

async function play(move, keepOutput) {
  const response = await fetch("/v1/move/validate", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ position, move })
  });
  const body = await response.json();
  if (!keepOutput)
    outputEl.textContent = JSON.stringify(body, null, 2);
  if (body.legal && body.position) {
    position = body.position;
    selected = null;
    await refresh();
  }
}

async function suggest() {
  statusEl.textContent = "Рушій думає…";
  const response = await fetch("/v1/move/suggest", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      gameId: "checkers-8x8",
      state: { notation: "PDN", position },
      level: levelEl.value
    })
  });
  const body = await response.json();
  outputEl.textContent = JSON.stringify(body, null, 2);
  if (response.ok && body.bestMove) {
    await play(body.bestMove, true);
    return;
  }
  await refresh();
}

refresh();
