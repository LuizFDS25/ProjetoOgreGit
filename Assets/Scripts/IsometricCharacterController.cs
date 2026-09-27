using System.Collections;
using UnityEngine;

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

    // Estado de movimento
    private Vector2 moveInput;
    private bool isRunning;
    private bool isMovingBackward;

    [Header("Run Backwards")]
    [Tooltip("Dot product entre movimento e direção que o personagem olha, abaixo disso = considerado 'pra trás'.")]
    public float backwardDotThreshold = -0.3f;

    // Última direção do personagem
    private Vector2 lastFacingDir = Vector2.down;

    // Estado de combo
    private int comboStep = 0;
    private bool comboQueued = false;
    private bool isAttacking = false;

    [Tooltip("Segurança: se um ataque ficar travado mais que isso (nem a Tag nem o Animation Event dispararam), libera o personagem sozinho. Deixe maior que a duração real dos seus clipes de ataque.")]
    public float maxAttackLockTime = 3f;
    private float attackLockTimer = 0f;
    private bool comboAdvancedThisAttack = false;

    // Estado de block
    private bool isBlocking = false;

    // Estado de ações especiais
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

    // Variação de Idle
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
        HandleResetInput();

        if (isDead) return;

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

    // MOVIMENTO
    private void ReadMovementInput()
    {
        float x = Input.GetAxisRaw("Horizontal");
        float y = Input.GetAxisRaw("Vertical");
        moveInput = new Vector2(x, y).normalized;

        isCrouching = Input.GetKey(KeyCode.LeftControl);
        isRunning = !isCrouching && Input.GetKey(KeyCode.LeftShift);
        animator.SetBool("IsCrouching", isCrouching);
    }

    // DIREÇÃO/BACKWARD
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

    private void UpdateFacingDirection()
    {
        if (isDashing) return;
        if (moveInput.sqrMagnitude < 0.01f) return;
        if (isMovingBackward) return;

        lastFacingDir = moveInput;
        animator.SetFloat("DirX", lastFacingDir.x);
        animator.SetFloat("DirY", lastFacingDir.y);
    }

    // COMBO DE ATAQUE
    private void HandleCombo()
    {
        if (Input.GetMouseButtonDown(0)) 
        {
            if (!isAttacking)
            {
                comboStep = 1;
                TriggerAttack(comboStep);
            }
            else if (comboStep < 3)
            {
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

    // ROLL E LONG ROLL

    private void HandleRoll()
    {
        if (Input.GetButtonDown("Jump"))
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

    // BLOCK
    private void HandleBlock()
    {
        bool holdingBlock = Input.GetMouseButton(1); 

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

    // IDLE VARIADO 
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

    // AÇÕES DEDICADAS
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

    // DANO E MORTE

    private void HandleDamageSimulation()
    {
        if (Input.GetKeyDown(takeDamageKey))
        {
            ReceiveHit();
        }
    }

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
        isPerformingAction = true;
        actionLockTimer = 0f;
        animator.SetTrigger("TakeDamage");
        PlayHitFlash();
    }

    public void Die()
    {
        isDead = true;

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