using System;
using System.Collections.Generic;
using Nintaco;
using ColdClearSharp;
using System.Runtime.InteropServices;
using System.Threading;
using System.Linq;
using System.Diagnostics;
using System.Text;

namespace ColdTaco
{
    class Addresses
    {
        public const int COPYRIGHT_UNSKIPPABLE = 0x00C3;
        public const int TETRIMINO_TYPE_TABLE = 0x993B;
        public const int CURRENT_PIECE_ID = 0x0042;
        public const int NEXT_PIECE_ID = 0x00BF;
        public const int GAME_STATE = 0x0048;
        public const int CLEARED_LINES = 0x0056;
        public const int BOARD = 0x0400;
        public const int LEVEL = 0x0044;
    }
    class Program
    {
        const int EMPTY_CELL = 0xEF;
        static CCPiece[] tetriminoMap;
        static Queue<int> inputs;
        static int currentInput;
        static IntPtr bot;
        const int thinkFrames = 3;
        static int prevGameState;
        static int framesSinceSpawn;
        static int[] lineClears;
        const int lineClearStatRectX = 185;
        const int lineClearStatRectY = 185;
        const int lineClearStatRectPadding = 2;
        static bool resetComplete = false;
        static CCMove move;
        static int currentLevel;
        static void Main(string[] args) {
            Init();
            ApiSource.initRemoteAPI("localhost", args.Length > 1 ? int.Parse(args[1]) : 9999);
            ApiSource.API.addFrameListener(RenderFinished);
            ApiSource.API.addStatusListener(StatusChanged);
            ApiSource.API.addActivateListener(ApiEnabled);
            ApiSource.API.addDeactivateListener(ApiDisabled);
            ApiSource.API.addStopListener(Dispose);
            ApiSource.API.run();
        }
        static void Init() {
            tetriminoMap = null;
            bot = IntPtr.Zero;
            inputs = new Queue<int>();
            currentInput = -1;
            prevGameState = 0;
            framesSinceSpawn = 0;
            lineClears = new int[4];
            move = new CCMove();
            currentLevel = 0;
        }
        static void InputPolled() {
            if (currentInput == -1) {
                if (inputs.TryDequeue(out currentInput)) {
                    ApiSource.API.writeGamepad(0, currentInput, true);
                } else {
                    ApiSource.API.writeGamepad(0, GamepadButtons.Down, framesSinceSpawn > thinkFrames);
                    currentInput = -1;
                }
            } else {
                ApiSource.API.writeGamepad(0, currentInput, false);
                currentInput = -1;
            }
        }
        static void RenderFinished() {
            static void ResetBot() {
                bool[] field = new bool[400];
                for (int y = 0; y < 20; y++) {
                    for (int x = 0; x < 10; x++) {
                        field[x + (19 - y) * 10] = ApiSource.API.peekCPU(Addresses.BOARD + x + y * 10) != EMPTY_CELL;
                    }
                }
                ColdClear.CcResetAsync(bot, field, false, 0);
            }
            int gameState = ApiSource.API.peekCPU(Addresses.GAME_STATE);
            if (resetComplete) {
                if (gameState == 0) {
                    return;
                }
            } else {
                if (gameState == 0) {
                    resetComplete = true;
                } else {
                    return;
                }
            }
            if (tetriminoMap == null) {
                tetriminoMap = new CCPiece[19];
                for (int i = 0; i < tetriminoMap.Length; i++) {
                    tetriminoMap[i] = ApiSource.API.peekCPU(Addresses.TETRIMINO_TYPE_TABLE + i) switch {
                        0 => CCPiece.CcT,
                        1 => CCPiece.CcJ,
                        2 => CCPiece.CcZ,
                        3 => CCPiece.CcO,
                        4 => CCPiece.CcS,
                        5 => CCPiece.CcL,
                        6 => CCPiece.CcI,
                        _ => throw new InvalidOperationException("Invalid piece type!")
                    };
                }
            }
            if (gameState == 4 && prevGameState != 4) {
                ++lineClears[ApiSource.API.peekCPU(Addresses.CLEARED_LINES) - 1];
            }
            if (gameState == 6 && prevGameState != 6) {
                int level = ApiSource.API.peekCPU(Addresses.LEVEL);
                if (currentLevel != level) {
                    currentLevel = level;
                    if (level == 29) {
                        ColdClear.CcDestroyAsync(bot);
                        CCOptions options = NESOptions();
                        CCWeights weights = NESWeights();
                        weights.Clear1 = 5;
                        weights.Clear2 = 10;
                        weights.Clear3 = 20;
                        weights.MaxWellDepth = 4;
                        bot = ColdClear.CcLaunchAsync(ref options, ref weights);
                        ResetBot();
                        ColdClear.CcAddNextPieceAsync(bot, tetriminoMap[ApiSource.API.peekCPU(Addresses.NEXT_PIECE_ID)]);
                    }
                }
            }
            if (prevGameState == 2 && gameState == 3) {
                for (int i = 0; i < 4; i++) {
                    int expectedX = move.ExpectedX[i];
                    int expectedY = 19 - move.ExpectedY[i];
                    if (ApiSource.API.peekCPU(Addresses.BOARD + expectedX + expectedY * 10) == EMPTY_CELL) {
                        inputs.Clear();
                        Console.WriteLine("Misdropped!");
                        ResetBot();
                        break;
                    }
                }
            }
            if (gameState == 1 && (prevGameState == 8 || prevGameState == 0)) {
                if (prevGameState == 0) {
                    ApiSource.API.addControllersListener(InputPolled);
                    ColdClear.CcAddNextPieceAsync(bot, tetriminoMap[ApiSource.API.peekCPU(Addresses.CURRENT_PIECE_ID)]);
                }
                ColdClear.CcAddNextPieceAsync(bot, tetriminoMap[ApiSource.API.peekCPU(Addresses.NEXT_PIECE_ID)]);
                framesSinceSpawn = 0;
            }
            if (framesSinceSpawn == thinkFrames) {
                CCPiece current = tetriminoMap[ApiSource.API.peekCPU(Addresses.CURRENT_PIECE_ID)];
                ColdClear.CcRequestNextMove(bot, 0);
                ColdClear.CcBlockNextMove(bot, out move, out _, 0);
                int movements = 0;
                int rotation = 0;
                foreach (CCMovement movement in move.Movements.Take(move.MovementCount)) {
                    if (current == CCPiece.CcI || current == CCPiece.CcS || current == CCPiece.CcZ) {
                        if (movement == CCMovement.CcCcw) {
                            switch (rotation) {
                                case 0:
                                    --movements;
                                    break;
                                case 3:
                                    ++movements;
                                    break;
                            }
                                
                        }
                        if (movement == CCMovement.CcCw) {
                            switch (rotation) {
                                case 2:
                                    --movements;
                                    break;
                                case 3:
                                    ++movements;
                                    break;
                            }
                        }
                    }
                    switch (movement) {
                        case CCMovement.CcCcw:
                            rotation = rotation > 0 ? (rotation - 1) : 3;
                            break;
                        case CCMovement.CcCw:
                            rotation = rotation < 3 ? (rotation + 1) : 0;
                            break;
                        case CCMovement.CcLeft:
                            --movements;
                            break;
                        case CCMovement.CcRight:
                            ++movements;
                            break;
                    }
                }
                if (current != CCPiece.CcI && current != CCPiece.CcO) {
                    if (current == CCPiece.CcJ || current == CCPiece.CcL || current == CCPiece.CcT) {
                        rotation = (rotation + 2) % 4;
                    }
                    --movements;
                }
                switch (rotation) {
                    case 1:
                        inputs.Enqueue(GamepadButtons.A);
                        break;
                    case 2:
                        inputs.Enqueue(GamepadButtons.A);
                        inputs.Enqueue(GamepadButtons.A);
                        break;
                    case 3:
                        inputs.Enqueue(GamepadButtons.B);
                        break;
                    default:
                        break;
                }
                while (movements != 0) {
                    if (movements > 0) {
                        inputs.Enqueue(GamepadButtons.Right);
                        --movements;
                    } else {
                        inputs.Enqueue(GamepadButtons.Left);
                        ++movements;
                    }
                }
            }
            ++framesSinceSpawn;
            prevGameState = gameState;
            ApiSource.API.setColor(Colors.BLACK);
            int width = ApiSource.API.getStringWidth("Quad 999", true);
            ApiSource.API.fillRect(lineClearStatRectX, lineClearStatRectY, width + lineClearStatRectPadding * 2, lineClears.Length * 9 + lineClearStatRectPadding * 2);
            ApiSource.API.setColor(Colors.WHITE);
            for (int i = 0; i < lineClears.Length; i++) {
                string text = i switch {
                    0 => "SING",
                    1 => "DOBL",
                    2 => "TRIP",
                    3 => "QUAD",
                    _ => "NANI"
                } + " " + lineClears[i].ToString("D3");
                ApiSource.API.drawString(text, lineClearStatRectX + lineClearStatRectPadding, lineClearStatRectY + (lineClears.Length - i - 1) * 9 + lineClearStatRectPadding, true);
            }
        }
        static void StatusChanged(string message) {
            Console.WriteLine("Status message: {0}", message);
        }
        static CCOptions NESOptions() {
            ColdClear.CcDefaultOptions(out CCOptions options);
            options.Mode = CCMovementMode.CcHardDropOnly;
            options.UseHold = false;
            options.Pcloop = false;
            options.Speculate = true;
            return options;
        }
        static CCWeights NESWeights() {
            ColdClear.CcDefaultWeights(out CCWeights weights);
            for (int i = 0; i < 4; i++) {
                weights.Tslot[i] = -1000;
            }
            weights.Jeopardy = -20;
            weights.TopHalf = -1000;
            weights.ComboGarbage = 0;
            weights.CavityCells = -173 * 2;
            weights.CavityCellsSq = -3;
            weights.OverhangCells = -173 * 2;
            weights.OverhangCellsSq = -3;
            weights.Clear1 = -71;
            weights.Clear2 = -50;
            weights.Clear3 = -29;
            weights.WellColumn[0] = 100;
            weights.WellColumn[9] = 100;
            weights.Bumpiness = -14;
            weights.BumpinessSq = -12;
            weights.WastedT = 0;
            weights.Height = -78;
            weights.MaxWellDepth = 10;
            return weights;
        }
        static void ApiEnabled() {
            Console.WriteLine("API enabled");
            ApiSource.API.reset();
            CCOptions options = NESOptions();
            CCWeights weights = NESWeights();
            bot = ColdClear.CcLaunchAsync(ref options, ref weights);
        }
        static void ApiDisabled() {
            ColdClear.CcDestroyAsync(bot);
            Init();
        }
        static void Dispose() {
            Console.WriteLine("API stopped");
        }
    }
}
