using System.Collections;
using UnityEngine;

/// <summary>
/// Controla um personagem isométrico onde:
///   - A DIREÇÃO visual (para onde o sprite olha) segue o próprio movimento (WASD).
///   - Correndo, se o jogador apertar a tecla de trás, o personagem mantém a
///     direção em que já estava olhando e usa a animação de Run Backwards
///     (desliza pra trás sem virar).
///   - Ataques formam um combo (1 -> 2 -> 3).
///   - Roll vira Long Roll automaticamente se o personagem estiver correndo.
///   - Ações especiais (Block, Cast Spell, Pummel, Quick Shot, Special 1/2)
///     usam botões dedicados.
///
/// Parâmetros esperados no Animator Controller:
///   Float  DirX, DirY        -> alimentam TODOS os Blend Trees 8-dir
///   Float  Speed              -> Idle / Walk / Run
///   Bool   IsRunning
///   Bool   IsMovingBackward    -> true quando corre na direção oposta à que olha
///   Bool   IsCrouching
///   Bool   IsBlocking
///   Trigger Attack1, Attack2, Attack3
///   Trigger Roll, LongRoll
///   Trigger CastSpell, Pummel, QuickShot, Special1, Special2
///   Trigger TakeDamage, Die, PlayIdle2
/// </summary>
[RequireComponent(typeof(Animator))]
public class IsometricCharacterController : MonoBehaviour
{
    [Header("Movimento")]
    public float walkSpeed = 3f;
    public float runSpeed = 6f;
    public float crouchSpeed = 1.5f;

    private bool isCrouching = false;

    private Animator animator;
    private Rigidbody2D rb;

    [Header("Feedback de Dano")]
    public Color hitFlashColor = Color.red;
    public float hitFlashDuration = 0.15f;
    private SpriteRenderer spriteRenderer;
    private Color originalSpriteColor;
    private Coroutine flashCoroutine;

    // --- Estado de movimento ---
    private Vector2 moveInput;
    private bool isRunning;
    private bool isMovingBackward;

    [Header("Run Backwards")]
    [Tooltip("Dot product entre movimento e direção que o personagem olha, abaixo disso = considerado 'pra trás'.")]
    public float backwardDotThreshold = -0.3f;

    // Última direção "de frente" do personagem (usada pelos Blend Trees)
    private Vector2 lastFacingDir = Vector2.down;

    // --- Estado de combo ---
    private int comboStep = 0;
    private bool comboQueued = false;
    private bool isAttacking = false;

    [Tooltip("Segurança: se um ataque ficar travado mais que isso (nem a Tag nem o Animation Event dispararam), libera o personagem sozinho. Deixe maior que a duração real dos seus clipes de ataque.")]
    public float maxAttackLockTime = 3f;
    private float attackLockTimer = 0f;
    private bool comboAdvancedThisAttack = false;

    // --- Estado de block ---
    private bool isBlocking = false;

    // --- Estado de ações especiais (Pummel, Quick Shot, Special 1/2) ---
    private bool isPerformingAction = false;
    private float actionLockTimer = 0f;
    [Tooltip("Segurança pras ações especiais, igual o do combo de ataque.")]
    public float maxActionLockTime = 3f;

    [Header("Long Roll (Dash)")]
    public float longRollDashDistance = 4f;
    public float longRollDashDuration = 0.5f;
    private bool isDashing = false;
    private Vector2 dashDirection;
    private float dashTimer = 0f;

    // --- Estado de idle variado ---
    [Header("Idle Variation")]
    [Tooltip("Intervalo mínimo/máximo (segundos) parado até tentar tocar o Idle2.")]
    public float idleVariantMinDelay = 4f;
    public float idleVariantMaxDelay = 9f;
    private float idleTimer = 0f;
    private float nextIdleVariantDelay;

    [Header("Dano / Morte (demo)")]
    public KeyCode takeDamageKey = KeyCode.T;
    public KeyCode resetKey = KeyCode.Backspace;
    public int hitsToDie = 3;
    private int hitCount = 0;
    private bool isDead = false;

    private void Awake()
    {
        animator = GetComponent<Animator>();
        rb = GetComponent<Rigidbody2D>();
        spriteRenderer = GetComponent<SpriteRenderer>();
        if (spriteRenderer != null) originalSpriteColor = spriteRenderer.color;
        nextIdleVariantDelay = Random.Range(idleVariantMinDelay, idleVariantMaxDelay);
    }

    private void Update()
    {
        HandleResetInput(); // funciona mesmo morto

        if (isDead) return; // trava tudo o resto

        ReadMovementInput();
        UpdateBackwardState();
        UpdateFacingDirection();
        HandleCombo();
        MonitorAttackProgress();
        HandleAttackSafety();
        HandleRoll();
        HandleDedicatedActions();
        HandleBlock();
        HandleIdleVariation();
        MonitorActionProgress();
        HandleDamageSimulation();

        animator.SetFloat("Speed", moveInput.magnitude);
        animator.SetBool("IsRunning", isRunning);
    }

    private void FixedUpdate()
    {
        if (isDead)
        {
            if (rb != null) rb.linearVelocity = Vector2.zero;
            return;
        }
        // Trava movimento durante ataque/block/ação especial, se quiser esse comportamento
        if (isAttacking || isBlocking || isPerformingAction)
        {
            if (rb != null) rb.linearVelocity = Vector2.zero;
            return;
        }

        if (isDashing)
        {
            HandleDashMovement();
            return;
        }

        float speed = isCrouching ? crouchSpeed : (isRunning ? runSpeed : walkSpeed);
        if (rb != null)
        {
            rb.linearVelocity = moveInput * speed;
        }
        else
        {
            transform.position += (Vector3)(moveInput * speed * Time.fixedDeltaTime);
        }
    }

    // ---------- MOVIMENTO ----------

    private void ReadMovementInput()
    {
        float x = Input.GetAxisRaw("Horizontal");
        float y = Input.GetAxisRaw("Vertical");
        moveInput = new Vector2(x, y).normalized;

        isCrouching = Input.GetKey(KeyCode.LeftControl);
        isRunning = !isCrouching && Input.GetKey(KeyCode.LeftShift);
        animator.SetBool("IsCrouching", isCrouching);
    }

    // ---------- DIREÇÃO / BACKWARD ----------

    /// <summary>
    /// Compara o movimento atual com a direção em que o personagem já está
    /// olhando. Se estiver correndo praticamente na direção oposta, marca
    /// como "andando de costas".
    /// </summary>
    private void UpdateBackwardState()
    {
        if (moveInput.sqrMagnitude < 0.01f)
        {
            isMovingBackward = false;
        }
        else
        {
            float dot = Vector2.Dot(moveInput, lastFacingDir);
            isMovingBackward = isRunning && dot < backwardDotThreshold;
        }

        animator.SetBool("IsMovingBackward", isMovingBackward);
    }

    /// <summary>
    /// Atualiza para onde o personagem "olha". Normalmente segue o próprio
    /// movimento. Exceção: se estiver correndo de costas, a direção fica
    /// travada (o personagem não vira, só desliza pra trás).
    /// </summary>
    private void UpdateFacingDirection()
    {
        if (isDashing) return; // direção travada durante o dash
        if (moveInput.sqrMagnitude < 0.01f) return; // parado: mantém a última direção
        if (isMovingBackward) return; // correndo de costas: não vira

        lastFacingDir = moveInput;
        animator.SetFloat("DirX", lastFacingDir.x);
        animator.SetFloat("DirY", lastFacingDir.y);
    }

    // ---------- COMBO DE ATAQUE ----------

    private void HandleCombo()
    {
        if (Input.GetMouseButtonDown(0)) // clique esquerdo do mouse (direto, sem passar pelo Fire1)
        {
            if (!isAttacking)
            {
                comboStep = 1;
                TriggerAttack(comboStep);
            }
            else if (comboStep < 3)
            {
                // Guarda a intenção de continuar o combo. Não depende de tempo:
                // só é consumida quando o Animation Event do fim do clipe atual
                // chamar OnAttackAnimationEnd().
                comboQueued = true;
                Debug.Log("[DEBUG] Segundo clique registrado. comboQueued = true, comboStep atual = " + comboStep);
            }
        }
    }

    private void TriggerAttack(int step)
    {
        isAttacking = true;
        comboQueued = false;
        attackLockTimer = 0f;
        comboAdvancedThisAttack = false;

        switch (step)
        {
            case 1: animator.SetTrigger("Attack1"); break;
            case 2: animator.SetTrigger("Attack2"); break;
            case 3: animator.SetTrigger("Attack3"); break;
        }
    }

    /// <summary>
    /// Chame este método via Animation Event no fim de cada clipe de ataque.
    /// </summary>
    public void OnAttackAnimationEnd()
    {
        Debug.Log("[DEBUG] OnAttackAnimationEnd chamado! comboQueued = " + comboQueued + ", comboStep = " + comboStep);

        if (comboQueued && comboStep < 3)
        {
            comboStep++;
            TriggerAttack(comboStep);
        }
        else
        {
            ResetCombo();
        }
    }

    private void ResetCombo()
    {
        comboStep = 0;
        comboQueued = false;
        isAttacking = false;
    }

    /// <summary>
    /// Rede de segurança: se o Animation Event de algum ataque não disparar
    /// (transição faltando no Animator, evento esquecido, etc.), isso libera
    /// o personagem sozinho depois de um tempo, em vez de travar pra sempre.
    /// </summary>
    private void HandleAttackSafety()
    {
        if (!isAttacking) return;

        attackLockTimer += Time.deltaTime;
        if (attackLockTimer > maxAttackLockTime)
        {
            Debug.LogWarning("Attack travou (Animation Event não disparou a tempo) — liberando o personagem. Confere as transições/eventos do Attack" + comboStep + " no Animator.");
            ResetCombo();
        }
    }

    /// <summary>
    /// Alternativa ao Animation Event: pergunta pro Animator se o estado
    /// atual tem a tag "Attack" e já passou de 90% da animação. Se sim,
    /// chama a mesma lógica de continuar/encerrar o combo.
    /// Marque a Tag "Attack" nos states Attack1, Attack2 e Attack3 no
    /// Animator (Inspector do state, campo "Tag") — não precisa mexer
    /// em cada um dos 8 clipes de direção.
    /// </summary>
    private void MonitorAttackProgress()
    {
        if (!isAttacking || comboAdvancedThisAttack) return;

        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
        if (info.IsTag("Attack") && info.normalizedTime >= 0.9f)
        {
            comboAdvancedThisAttack = true;
            OnAttackAnimationEnd();
        }
    }

    // ---------- ROLL / LONG ROLL ----------

    private void HandleRoll()
    {
        if (Input.GetButtonDown("Jump")) // exemplo: barra de espaço
        {
            if (isRunning)
            {
                animator.SetTrigger("LongRoll");
                StartDash();
            }
            else
            {
                animator.SetTrigger("Roll");
            }
        }
    }

    /// <summary>
    /// Trava a direção atual e move o personagem uma distância fixa,
    /// ignorando o input do jogador durante o tempo do dash.
    /// </summary>
    private void StartDash()
    {
        isDashing = true;
        dashDirection = lastFacingDir;
        dashTimer = 0f;
    }

    private void HandleDashMovement()
    {
        dashTimer += Time.fixedDeltaTime;

        float dashSpeed = longRollDashDistance / longRollDashDuration;
        rb.linearVelocity = dashDirection * dashSpeed;

        if (dashTimer >= longRollDashDuration)
        {
            isDashing = false;
            rb.linearVelocity = Vector2.zero;
        }
    }

    // ---------- BLOCK ----------

    private void HandleBlock()
    {
        bool holdingBlock = Input.GetMouseButton(1); // botão direito do mouse

        if (holdingBlock && !isBlocking)
        {
            isBlocking = true;
            animator.SetBool("IsBlocking", true);
            animator.SetTrigger("EnterBlock");
        }
        else if (!holdingBlock && isBlocking)
        {
            isBlocking = false;
            animator.SetBool("IsBlocking", false);
        }
    }

    // ---------- IDLE VARIADO ----------

    private void HandleIdleVariation()
    {
        bool isStandingStill = moveInput.sqrMagnitude < 0.01f
            && !isAttacking && !isBlocking;

        if (!isStandingStill)
        {
            idleTimer = 0f;
            return;
        }

        idleTimer += Time.deltaTime;

        if (idleTimer >= nextIdleVariantDelay)
        {
            animator.SetTrigger("PlayIdle2");
            idleTimer = 0f;
            nextIdleVariantDelay = Random.Range(idleVariantMinDelay, idleVariantMaxDelay);
        }
    }

    // ---------- AÇÕES DEDICADAS ----------

    private void HandleDedicatedActions()
    {
        if (Input.GetKeyDown(KeyCode.Q)) TriggerDedicatedAction("CastSpell");
        if (Input.GetKeyDown(KeyCode.E)) TriggerDedicatedAction("Pummel");
        if (Input.GetKeyDown(KeyCode.R)) TriggerDedicatedAction("QuickShot");
        if (Input.GetKeyDown(KeyCode.Alpha1)) TriggerDedicatedAction("Special1");
        if (Input.GetKeyDown(KeyCode.Alpha2)) TriggerDedicatedAction("Special2");
    }

    private void TriggerDedicatedAction(string triggerName)
    {
        isPerformingAction = true;
        actionLockTimer = 0f;
        animator.SetTrigger(triggerName);
    }

    /// <summary>
    /// Mesmo esquema do MonitorAttackProgress: pergunta pro Animator se o
    /// estado atual tem a tag "Action" e já passou de 90% da animação.
    /// Marque a Tag "Action" nos states Pummel, QuickShot, Special1 e
    /// Special2 no Animator.
    /// </summary>
    private void MonitorActionProgress()
    {
        if (!isPerformingAction) return;

        AnimatorStateInfo info = animator.GetCurrentAnimatorStateInfo(0);
        if (info.IsTag("Action") && info.normalizedTime >= 0.9f)
        {
            isPerformingAction = false;
            return;
        }

        actionLockTimer += Time.deltaTime;
        if (actionLockTimer > maxActionLockTime)
        {
            Debug.LogWarning("Ação especial travou (Tag 'Action' não encontrada a tempo) — liberando o personagem.");
            isPerformingAction = false;
        }
    }

    // ---------- DANO / MORTE (demo) ----------

    private void HandleDamageSimulation()
    {
        if (Input.GetKeyDown(takeDamageKey))
        {
            ReceiveHit();
        }
    }

    /// <summary>
    /// Ponto de entrada pra "tomar um hit". Chame isso a partir de qualquer
    /// lugar (botão de UI, inimigo, etc.) — não chame TakeDamage()/Die()
    /// direto se quiser que o contador de hits funcione.
    /// </summary>
    public void ReceiveHit()
    {
        if (isDead) return;

        hitCount++;

        if (hitCount >= hitsToDie)
        {
            Die();
        }
        else
        {
            TakeDamage();
        }
    }

    public void TakeDamage()
    {
        // Reaproveita o mesmo esquema de trava por Tag do Attack/Action.
        // Marque a Tag "Action" no state TakeDamage no Animator.
        isPerformingAction = true;
        actionLockTimer = 0f;
        animator.SetTrigger("TakeDamage");
        PlayHitFlash();
    }

    public void Die()
    {
        isDead = true;

        // Limpa qualquer trava/estado que possa ter ficado no meio
        isAttacking = false;
        comboStep = 0;
        comboQueued = false;
        isPerformingAction = false;
        isDashing = false;
        isBlocking = false;

        if (rb != null) rb.linearVelocity = Vector2.zero;
        animator.SetTrigger("Die");
        PlayHitFlash();
    }

    private void PlayHitFlash()
    {
        if (spriteRenderer == null) return;

        if (flashCoroutine != null) StopCoroutine(flashCoroutine);
        flashCoroutine = StartCoroutine(HitFlashRoutine());
    }

    private IEnumerator HitFlashRoutine()
    {
        spriteRenderer.color = hitFlashColor;
        yield return new WaitForSeconds(hitFlashDuration);
        spriteRenderer.color = originalSpriteColor;
        flashCoroutine = null;
    }

    private void HandleResetInput()
    {
        if (Input.GetKeyDown(resetKey))
        {
            ResetCharacter();
        }
    }

    /// <summary>
    /// Reseta o personagem pro estado inicial: zera contador de hits,
    /// destrava tudo e usa Animator.Rebind() pra voltar o Animator inteiro
    /// pro estado padrão (equivalente a como ele estava no Play).
    /// </summary>
    public void ResetCharacter()
    {
        isDead = false;
        hitCount = 0;
        isAttacking = false;
        comboStep = 0;
        comboQueued = false;
        isBlocking = false;
        isPerformingAction = false;
        isDashing = false;
        isCrouching = false;
        isRunning = false;
        isMovingBackward = false;
        moveInput = Vector2.zero;
        idleTimer = 0f;

        if (rb != null) rb.linearVelocity = Vector2.zero;

        if (flashCoroutine != null)
        {
            StopCoroutine(flashCoroutine);
            flashCoroutine = null;
        }
        if (spriteRenderer != null) spriteRenderer.color = originalSpriteColor;

        animator.Rebind();
        animator.Update(0f);
    }
}